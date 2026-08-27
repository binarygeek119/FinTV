using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Reads chapter markers from local video files with ffprobe and stores them on the catalog.
/// </summary>
public sealed class CatalogChapterProbeService
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

    private readonly FinTvDbContext _db;
    private readonly PathRemapService _remap;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ILogger<CatalogChapterProbeService> _logger;

    public CatalogChapterProbeService(
        FinTvDbContext db,
        PathRemapService remap,
        IFfmpegLocator ffmpeg,
        ILogger<CatalogChapterProbeService> logger)
    {
        _db = db;
        _remap = remap;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<CatalogChapterProbeResult> ProbeAsync(
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
            query = query.Where(item => item.FfprobeChaptersAt == null
                && !_db.MediaChapters.Any(chapter => chapter.MediaItemId == item.Id));
        }

        var rows = await query
            .Select(item => new ProbeTarget(item.Id, item.Path!, item.SourceConnectionId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var targets = new List<(Guid Id, string Path)>();
        var skipped = 0;
        foreach (var row in rows)
        {
            var path = _remap.ResolveExistingPath(row.Path, row.SourceConnectionId);
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || NonVideoExtensions.Contains(Path.GetExtension(path)))
            {
                skipped++;
                continue;
            }

            targets.Add((row.Id, path));
        }

        var total = targets.Count;
        onProgress?.Invoke(0, total, 0);
        if (total == 0)
        {
            return new CatalogChapterProbeResult(0, 0, skipped, 0);
        }

        var found = new ConcurrentBag<(Guid Id, List<ProbedChapter> Chapters)>();
        var failed = 0;
        var processed = 0;
        using var gate = new SemaphoreSlim(2, 2);
        var tasks = targets.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapters = await ReadChaptersAsync(row.Path, cancellationToken).ConfigureAwait(false);
                if (chapters is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                found.Add((row.Id, chapters));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _logger.LogDebug(ex, "ffprobe chapter read failed for {Path}", row.Path);
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
        var wroteChapters = 0;
        foreach (var batch in found.Chunk(25))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (id, chapters) in batch)
            {
                if (chapters.Count > 0)
                {
                    wroteChapters++;
                    await _db.MediaChapters.Where(chapter => chapter.MediaItemId == id)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var chapter in chapters)
                    {
                        _db.MediaChapters.Add(new MediaChapter
                        {
                            MediaItemId = id,
                            StartPositionTicks = chapter.StartPositionTicks,
                            Name = chapter.Name
                        });
                    }

                    var json = JsonSerializer.Serialize(chapters.Select(chapter => new
                    {
                        startPositionTicks = chapter.StartPositionTicks,
                        name = chapter.Name
                    }));
                    await WriteTypedChaptersJsonAsync(id, json, cancellationToken).ConfigureAwait(false);
                }

                await _db.MediaItems
                    .Where(item => item.Id == id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(item => item.FfprobeChaptersAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await _db.SaveChangesIgnoringGoneRowsAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            onProgress?.Invoke(Math.Min(total, processed), total, wroteChapters);
        }

        _logger.LogInformation(
            "ffprobe chapter scan finished: {Probed} videos, {WithChapters} with chapters, {Skipped} skipped, {Failed} failed",
            found.Count,
            wroteChapters,
            skipped,
            failed);
        return new CatalogChapterProbeResult(found.Count, wroteChapters, skipped, failed);
    }

    private async Task WriteTypedChaptersJsonAsync(Guid id, string json, CancellationToken cancellationToken)
    {
        await _db.TvShows.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChaptersJson, json), cancellationToken)
            .ConfigureAwait(false);
        await _db.Episodes.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChaptersJson, json), cancellationToken)
            .ConfigureAwait(false);
        await _db.Movies.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChaptersJson, json), cancellationToken)
            .ConfigureAwait(false);
        await _db.MusicVideos.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChaptersJson, json), cancellationToken)
            .ConfigureAwait(false);
        await _db.PastTenseNews.Where(row => row.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChaptersJson, json), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<List<ProbedChapter>?> ReadChaptersAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolveFfprobe(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-print_format");
        start.ArgumentList.Add("json");
        start.ArgumentList.Add("-show_chapters");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
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

            return null;
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        return ParseChapters(stdout.ToString());
    }

    private static List<ProbedChapter> ParseChapters(string json)
    {
        var list = new List<ProbedChapter>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chapters", out var chapters)
                || chapters.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var chapter in chapters.EnumerateArray())
            {
                var seconds = ReadStartSeconds(chapter);
                if (seconds is null)
                {
                    continue;
                }

                var ticks = (long)Math.Round(seconds.Value * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
                string? name = null;
                if (chapter.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    name = ReadString(tags, "title") ?? ReadString(tags, "TITLE");
                }

                list.Add(new ProbedChapter(ticks, name));
            }
        }
        catch (JsonException)
        {
            return list;
        }

        return list;
    }

    private static double? ReadStartSeconds(JsonElement chapter)
    {
        if (chapter.TryGetProperty("start_time", out var startTime))
        {
            if (startTime.ValueKind == JsonValueKind.Number && startTime.TryGetDouble(out var number))
            {
                return number;
            }

            if (startTime.ValueKind == JsonValueKind.String
                && double.TryParse(startTime.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        if (chapter.TryGetProperty("start", out var start) && start.TryGetInt64(out var raw)
            && chapter.TryGetProperty("time_base", out var timeBase)
            && timeBase.ValueKind == JsonValueKind.String)
        {
            var text = timeBase.GetString();
            var parts = text?.Split('/');
            if (parts is { Length: 2 }
                && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var num)
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var den)
                && den != 0)
            {
                return raw * (double)num / den;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string ResolveFfprobe()
    {
        var dir = Path.GetDirectoryName(_ffmpeg.EncoderPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return "ffprobe";
    }

    private sealed record ProbeTarget(Guid Id, string Path, Guid? SourceConnectionId);

    private sealed record ProbedChapter(long StartPositionTicks, string? Name);
}

public sealed record CatalogChapterProbeResult(int Probed, int WithChapters, int Skipped, int Failed);
