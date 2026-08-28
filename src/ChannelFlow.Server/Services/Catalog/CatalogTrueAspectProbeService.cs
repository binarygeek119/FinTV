using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Samples five points in each video with ffmpeg cropdetect to measure the active picture inside the raster.
/// </summary>
public sealed class CatalogTrueAspectProbeService
{
    private static readonly BaseItemKind[] VideoKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.MusicVideo
    ];

    private static readonly HashSet<string> NonVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg",
        ".mp3", ".flac", ".m4a", ".wav", ".aac", ".ogg", ".opus", ".wma",
        ".nfo", ".txt", ".xml"
    };

    private static readonly double[] SampleFractions = [0.10, 0.25, 0.50, 0.75, 0.90];

    private static readonly Regex CropLine = new(
        @"crop=(\d+):(\d+):(\d+):(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FinTvDbContext _db;
    private readonly PathRemapService _remap;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ILogger<CatalogTrueAspectProbeService> _logger;

    public CatalogTrueAspectProbeService(
        FinTvDbContext db,
        PathRemapService remap,
        IFfmpegLocator ffmpeg,
        ILogger<CatalogTrueAspectProbeService> logger)
    {
        _db = db;
        _remap = remap;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<CatalogTrueAspectProbeResult> ProbeAsync(
        IReadOnlyCollection<Guid>? itemIds,
        bool missingOnly,
        Action<int, int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        var query = _db.MediaItems.AsNoTracking()
            .Where(item => !item.IsMissing
                && VideoKinds.Contains(item.Kind)
                && item.Path != null
                && item.Path != "");
        if (itemIds is { Count: > 0 })
        {
            query = query.Where(item => itemIds.Contains(item.Id));
        }

        if (missingOnly)
        {
            query = query.Where(item => item.TrueAspectRatio == null);
        }

        var rows = await query
            .Select(item => new ProbeTarget(item.Id, item.Path!, item.SourceConnectionId, item.RuntimeTicks))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var targets = new List<ProbeTarget>();
        var skipped = 0;
        string? skipExample = null;
        foreach (var row in rows)
        {
            var remapped = _remap.Remap(row.Path, null, row.SourceConnectionId);
            var path = _remap.ResolveExistingFile(row.Path, row.SourceConnectionId);
            if (string.IsNullOrWhiteSpace(path)
                || NonVideoExtensions.Contains(Path.GetExtension(path)))
            {
                skipped++;
                skipExample ??= (row.Path ?? "") + " → " + (remapped ?? "(none)");
                continue;
            }

            targets.Add(row with { Path = path });
        }

        var total = targets.Count;
        onProgress?.Invoke(0, total, 0);
        if (total == 0)
        {
            if (skipped > 0)
            {
                _logger.LogWarning(
                    "True-aspect skipped all {Skipped} videos; remapped files were not on disk. Example: {Example}. Check Library path remaps.",
                    skipped,
                    skipExample);
            }

            return new CatalogTrueAspectProbeResult(0, 0, skipped, 0, skipExample);
        }

        var found = new ConcurrentBag<(Guid Id, string Ratio)>();
        var failed = 0;
        var processed = 0;
        using var gate = new SemaphoreSlim(1, 1);
        var tasks = targets.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ratio = await MeasureAsync(row.Path, row.RuntimeTicks, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(ratio))
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                found.Add((row.Id, ratio));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _logger.LogDebug(ex, "True-aspect cropdetect failed for {Path}", row.Path);
            }
            finally
            {
                var done = Interlocked.Increment(ref processed);
                onProgress?.Invoke(done, total, 0);
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var wrote = 0;
        foreach (var batch in found.Chunk(25))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (id, ratio) in batch)
            {
                wrote++;
                await _db.MediaItems
                    .Where(item => item.Id == id)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(item => item.TrueAspectRatio, ratio)
                            .SetProperty(item => item.TrueAspectProbedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteTypedTrueAspectAsync(id, ratio, cancellationToken).ConfigureAwait(false);
            }

            _db.ChangeTracker.Clear();
            onProgress?.Invoke(Math.Min(total, processed), total, wrote);
        }

        _logger.LogInformation(
            "True-aspect scan finished: {Probed} videos, {Wrote} measured, {Skipped} skipped, {Failed} failed",
            found.Count,
            wrote,
            skipped,
            failed);
        return new CatalogTrueAspectProbeResult(found.Count, wrote, skipped, failed, skipExample);
    }

    private async Task WriteTypedTrueAspectAsync(Guid id, string ratio, CancellationToken cancellationToken)
    {
        await _db.TvShows.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TrueAspectRatio, ratio), cancellationToken)
            .ConfigureAwait(false);
        await _db.Episodes.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TrueAspectRatio, ratio), cancellationToken)
            .ConfigureAwait(false);
        await _db.Movies.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TrueAspectRatio, ratio), cancellationToken)
            .ConfigureAwait(false);
        await _db.MusicVideos.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TrueAspectRatio, ratio), cancellationToken)
            .ConfigureAwait(false);
        await _db.PastTenseNews.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TrueAspectRatio, ratio), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> MeasureAsync(string path, long? runtimeTicks, CancellationToken cancellationToken)
    {
        var durationSeconds = runtimeTicks is > 0
            ? TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds
            : 0;
        var seeks = SampleTimes(durationSeconds);
        var crops = new List<(int Width, int Height)>();
        foreach (var seek in seeks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crop = await CropDetectAsync(path, seek, cancellationToken).ConfigureAwait(false);
            if (crop is { Width: > 0, Height: > 0 })
            {
                crops.Add(crop.Value);
            }
        }

        return VideoAspectFormat.FromActivePictureSamples(crops);
    }

    private static IReadOnlyList<double> SampleTimes(double durationSeconds)
    {
        if (durationSeconds < 2)
        {
            return [0];
        }

        var pad = Math.Min(1.0, durationSeconds / 10);
        var maxSeek = Math.Max(0, durationSeconds - pad);
        var times = new List<double>(SampleFractions.Length);
        foreach (var fraction in SampleFractions)
        {
            var seek = Math.Clamp(durationSeconds * fraction, 0, maxSeek);
            times.Add(seek);
        }

        return times;
    }

    private async Task<(int Width, int Height)?> CropDetectAsync(
        string path,
        double seekSeconds,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(_ffmpeg.EncoderPath) ? "ffmpeg" : _ffmpeg.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-nostdin");
        start.ArgumentList.Add("-ss");
        start.ArgumentList.Add(seekSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-an");
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("6");
        start.ArgumentList.Add("-vf");
        start.ArgumentList.Add("cropdetect=24:2:0");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("null");
        start.ArgumentList.Add("-");

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            return ParseLastCrop(stderr.ToString());
        }

        return ParseLastCrop(stderr.ToString());
    }

    internal static (int Width, int Height)? ParseLastCrop(string stderr)
    {
        Match? last = null;
        foreach (Match match in CropLine.Matches(stderr))
        {
            last = match;
        }

        if (last is null
            || !int.TryParse(last.Groups[1].Value, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(last.Groups[2].Value, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    private sealed record ProbeTarget(Guid Id, string Path, Guid? SourceConnectionId, long? RuntimeTicks);
}

public sealed record CatalogTrueAspectProbeResult(int Probed, int Measured, int Skipped, int Failed, string? SkipExample = null);
