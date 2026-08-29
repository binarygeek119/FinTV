using System.Text.Json;
using ChannelFlow.CommercialDetect;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Detects commercial-break spots one file at a time (same rules as Commercial Spot Tester) and inserts numbered break chapters.
/// </summary>
public sealed class CatalogCommercialBreakProbeService
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
    private readonly ILogger<CatalogCommercialBreakProbeService> _logger;
    private readonly CommercialSpotDetector _detector = new();

    public CatalogCommercialBreakProbeService(
        FinTvDbContext db,
        PathRemapService remap,
        IFfmpegLocator ffmpeg,
        ILogger<CatalogCommercialBreakProbeService> logger)
    {
        _db = db;
        _remap = remap;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<CatalogCommercialBreakProbeResult> ProbeAsync(
        CommercialBreakScanSettings settings,
        bool missingOnly,
        Action<CatalogCommercialBreakProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        settings.Clamp();
        var commercialTags = CommercialTags(FinTvRuntime.Current?.Configuration.CommercialLibraryTag);
        var query = _db.MediaItems.AsNoTracking()
            .Where(item => !item.IsMissing
                && VideoKinds.Contains(item.Kind)
                && item.Path != null
                && item.Path != "");
        if (missingOnly)
        {
            query = query.Where(item => item.CommercialBreaksProbedAt == null);
        }

        var rows = await query
            .Select(item => new ProbeTarget(item.Id, item.Path!, item.SourceConnectionId, item.TagsJson, item.RuntimeTicks))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var targets = new List<(Guid Id, string Path)>();
        var skipped = 0;
        string? skipExample = null;
        foreach (var row in rows)
        {
            if (HasCommercialTag(row.TagsJson, commercialTags))
            {
                skipped++;
                continue;
            }

            var remapped = _remap.Remap(row.Path, null, row.SourceConnectionId);
            var path = _remap.ResolveExistingFile(row.Path, row.SourceConnectionId);
            if (string.IsNullOrWhiteSpace(path)
                || NonVideoExtensions.Contains(Path.GetExtension(path)))
            {
                skipped++;
                skipExample ??= (row.Path ?? "") + " → " + (remapped ?? "(none)");
                continue;
            }

            targets.Add((row.Id, path));
        }

        var total = targets.Count;
        onProgress?.Invoke(new CatalogCommercialBreakProgress(0, total, 0));
        if (total == 0)
        {
            return new CatalogCommercialBreakProbeResult(0, 0, skipped, 0, 0, skipExample);
        }

        var ffmpegPath = _ffmpeg.EncoderPath;
        var ffprobePath = FfmpegTools.ResolveFfprobe(ffmpegPath);
        var probed = 0;
        var added = 0;
        var wroteFiles = 0;
        var failed = 0;
        var processed = 0;

        foreach (var (id, path) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = await _db.MediaChapters.AsNoTracking()
                    .Where(chapter => chapter.MediaItemId == id)
                    .Select(chapter => new { chapter.Name, chapter.StartPositionTicks })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var fileName = Path.GetFileName(path);
                onProgress?.Invoke(new CatalogCommercialBreakProgress(processed, total, added, fileName, "Reading file", 0));
                var fileProgress = new Progress<ScanProgress>(update =>
                    onProgress?.Invoke(new CatalogCommercialBreakProgress(
                        processed,
                        total,
                        added,
                        fileName,
                        update.Phase,
                        update.Fraction)));
                var result = await _detector.DetectAsync(
                        path,
                        ffmpegPath,
                        ffprobePath,
                        settings,
                        existing.Select(chapter => chapter.Name),
                        progress: fileProgress,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var addedHere = 0;
                if (result.Disposition != FileDisposition.SkipFile && result.Accepted.Count > 0)
                {
                    var insert = await InsertBreaksAsync(
                            id,
                            path,
                            result,
                            existing.Select(chapter => chapter.StartPositionTicks).ToList(),
                            settings.WriteChaptersToFiles,
                            cancellationToken)
                        .ConfigureAwait(false);
                    addedHere = insert.Added;
                    added += insert.Added;
                    if (insert.WroteFile)
                    {
                        wroteFiles++;
                    }
                }

                await _db.MediaItems
                    .Where(item => item.Id == id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(item => item.CommercialBreaksProbedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
                probed++;
                _logger.LogDebug(
                    "Commercial-break scan {Path}: {Disposition}, {Accepted} accepted, {Added} added",
                    path,
                    result.Disposition,
                    result.Accepted.Count,
                    addedHere);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogDebug(ex, "Commercial-break scan failed for {Path}", path);
            }
            finally
            {
                processed++;
                onProgress?.Invoke(new CatalogCommercialBreakProgress(processed, total, added, Path.GetFileName(path), null, 1));
            }
        }

        _logger.LogInformation(
            "Commercial-break scan finished: {Probed} videos, {Added} breaks added, {Wrote} files rewritten, {Skipped} skipped, {Failed} failed",
            probed,
            added,
            wroteFiles,
            skipped,
            failed);
        return new CatalogCommercialBreakProbeResult(probed, added, skipped, failed, wroteFiles, skipExample);
    }

    private async Task<(int Added, bool WroteFile)> InsertBreaksAsync(
        Guid id,
        string path,
        DetectResult result,
        IReadOnlyList<long> existingStarts,
        bool writeFiles,
        CancellationToken cancellationToken)
    {
        var existingTicks = existingStarts.ToHashSet();
        var added = 0;
        var toWrite = result.Probe.Chapters.ToList();
        foreach (var spot in result.Accepted)
        {
            var ticks = ToTicks(spot.AtSeconds);
            if (existingTicks.Any(existing => Math.Abs(existing - ticks) < TimeSpan.TicksPerSecond / 2))
            {
                continue;
            }

            var end = spot.AtSeconds + Math.Max(0.5, spot.Black?.Duration ?? 1);
            toWrite.Add(new FileChapter
            {
                StartSeconds = spot.AtSeconds,
                EndSeconds = end,
                Name = spot.Name
            });
            _db.MediaChapters.Add(new MediaChapter
            {
                MediaItemId = id,
                StartPositionTicks = ticks,
                Name = spot.Name
            });
            existingTicks.Add(ticks);
            added++;
        }

        if (added == 0)
        {
            return (0, false);
        }

        await _db.SaveChangesIgnoringGoneRowsAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();

        var all = await _db.MediaChapters.AsNoTracking()
            .Where(chapter => chapter.MediaItemId == id)
            .OrderBy(chapter => chapter.StartPositionTicks)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var json = JsonSerializer.Serialize(all.Select(chapter => new
        {
            startPositionTicks = chapter.StartPositionTicks,
            name = chapter.Name
        }));
        await WriteTypedChaptersJsonAsync(id, json, cancellationToken).ConfigureAwait(false);

        var wroteFile = false;
        if (writeFiles && FfmpegTools.SupportsChapterRewrite(path) && FfmpegTools.CanWriteInPlace(path))
        {
            try
            {
                await FfmpegTools.WriteChaptersAsync(_ffmpeg.EncoderPath, path, toWrite, cancellationToken)
                    .ConfigureAwait(false);
                wroteFile = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write break chapters into {Path}", path);
            }
        }

        return (added, wroteFile);
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

    private static HashSet<string> CommercialTags(string? extra)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "channelflow-commercial",
            "fintv-commercial"
        };
        if (!string.IsNullOrWhiteSpace(extra))
        {
            tags.Add(extra.Trim());
        }

        return tags;
    }

    private static bool HasCommercialTag(string? tagsJson, HashSet<string> tags)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return tags.Any(tag => tagsJson.Contains(tag, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var value in doc.RootElement.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String
                    && tags.Contains(value.GetString() ?? ""))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return tags.Any(tag => tagsJson.Contains(tag, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static long ToTicks(double seconds)
        => (long)Math.Round(Math.Max(0, seconds) * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);

    private sealed record ProbeTarget(Guid Id, string Path, Guid? SourceConnectionId, string TagsJson, long? RuntimeTicks);
}

public sealed record CatalogCommercialBreakProgress(
    int Processed,
    int Total,
    int Found,
    string? FileName = null,
    string? Phase = null,
    double FileFraction = 0);

public sealed record CatalogCommercialBreakProbeResult(
    int Probed,
    int Added,
    int Skipped,
    int Failed,
    int WroteFiles,
    string? SkipExample = null);
