using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

internal static class LineupSlotKinds
{
    public const string Movie = "movie";

    public const string TvShow = "tvshow";

    public const string ShortBlock = "short-block";
}

/// <summary>
/// Colors Lineups timeslots from playout (packed shorts) and catalog item kind (movie vs TV).
/// </summary>
public sealed class LineupSlotKindService
{
    private readonly FinTvDbContext _db;

    public LineupSlotKindService(FinTvDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Classifies each lineup slot as movie, TV show, or packed short-episode block.
    /// Playout for <paramref name="date"/> wins; catalog candidates fill empty coverage.
    /// </summary>
    public async Task<Dictionary<int, string>> ClassifyAsync(
        Guid channelId,
        ChannelContentType contentType,
        IEnumerable<LineupSlot>? slots,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        if (contentType is ChannelContentType.Music
            or ChannelContentType.MusicVideo
            or ChannelContentType.Weather
            or ChannelContentType.News)
        {
            return result;
        }

        var slotList = (slots ?? []).OrderBy(s => s.SlotIndex).ToList();
        if (slotList.Count == 0)
        {
            return result;
        }

        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        var dayStartUtc = LocalToUtc(date, 0, tz);
        var dayEndUtc = LocalToUtc(date.AddDays(1), 0, tz);

        var items = await _db.PlayoutItems.AsNoTracking()
            .Where(p =>
                p.ChannelId == channelId
                && p.Finish > dayStartUtc
                && p.Start < dayEndUtc
                && p.CommercialId == null
                && !p.IsVirtual
                && (p.GuideGroup == null
                    || (p.GuideGroup != "commercial" && p.GuideGroup != LogoBumperService.GuideGroup)))
            .OrderBy(p => p.Start)
            .ToListAsync(cancellationToken);
        items = MergeSplitPrograms(items);

        var candidateIds = slotList
            .SelectMany(s => s.Candidates)
            .Where(c => c.JellyfinItemId.HasValue)
            .Select(c => c.JellyfinItemId!.Value)
            .Distinct()
            .ToList();
        var playoutIds = items
            .Where(i => i.JellyfinItemId.HasValue)
            .Select(i => i.JellyfinItemId!.Value);
        var allIds = candidateIds.Concat(playoutIds).Distinct().ToList();

        var catalog = await LoadCatalogAsync(allIds, cancellationToken);
        var packedIds = ShortEpisodeBlocks.FindPackedShortPlayoutIds(items, catalog.Episodes);

        foreach (var slot in slotList)
        {
            if (slot.IsRerunSlot)
            {
                continue;
            }

            var startMinutes = Math.Clamp(slot.SlotIndex, 0, 47) * 30;
            var spanMinutes = Math.Max(1, slot.SpanSlots) * 30;
            var slotStart = LocalToUtc(date, startMinutes, tz);
            var slotEnd = LocalToUtc(date, Math.Min(24 * 60, startMinutes + spanMinutes), tz);
            if (slotEnd <= slotStart)
            {
                slotEnd = slotStart.AddMinutes(spanMinutes);
            }

            var overlapping = items
                .Where(p => p.Start < slotEnd && p.Finish > slotStart)
                .ToList();
            if (overlapping.Count > 0)
            {
                if (overlapping.Any(p => packedIds.Contains(p.Id)))
                {
                    result[slot.SlotIndex] = LineupSlotKinds.ShortBlock;
                    continue;
                }

                var primary = overlapping
                    .OrderByDescending(p => Overlap(p.Start, p.Finish, slotStart, slotEnd))
                    .First();
                var fromItem = KindFromItemId(primary.JellyfinItemId, catalog, packedFromCandidate: false);
                if (fromItem is not null)
                {
                    result[slot.SlotIndex] = fromItem;
                    continue;
                }
            }

            var fromCandidate = KindFromSlotCandidates(slot, catalog, contentType);
            if (fromCandidate is not null)
            {
                result[slot.SlotIndex] = fromCandidate;
            }
        }

        return result;
    }

    private async Task<CatalogKinds> LoadCatalogAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var catalog = new CatalogKinds();
        if (ids.Count == 0)
        {
            return catalog;
        }

        var movies = await _db.Movies.AsNoTracking()
            .Where(m => ids.Contains(m.Id) || ids.Contains(m.JellyfinItemId))
            .Select(m => new { m.Id, m.JellyfinItemId })
            .ToListAsync(cancellationToken);
        foreach (var movie in movies)
        {
            catalog.Movies.Add(movie.Id);
            catalog.Movies.Add(movie.JellyfinItemId);
        }

        var videos = await _db.MusicVideos.AsNoTracking()
            .Where(v => ids.Contains(v.Id) || ids.Contains(v.JellyfinItemId))
            .Select(v => new { v.Id, v.JellyfinItemId })
            .ToListAsync(cancellationToken);
        foreach (var video in videos)
        {
            catalog.MusicVideos.Add(video.Id);
            catalog.MusicVideos.Add(video.JellyfinItemId);
        }

        var shows = await _db.TvShows.AsNoTracking()
            .Where(s => ids.Contains(s.Id) || ids.Contains(s.JellyfinItemId))
            .Select(s => new { s.Id, s.JellyfinItemId })
            .ToListAsync(cancellationToken);
        foreach (var show in shows)
        {
            catalog.Series.Add(show.Id);
            catalog.Series.Add(show.JellyfinItemId);
        }

        var episodeRows = await _db.Episodes.AsNoTracking()
            .Where(e => ids.Contains(e.Id) || ids.Contains(e.JellyfinItemId))
            .Select(e => new { e.Id, e.JellyfinItemId, e.SeriesId, e.RuntimeTicks })
            .ToListAsync(cancellationToken);
        foreach (var row in episodeRows)
        {
            var info = new ShortEpisodeCatalogInfo(
                row.SeriesId ?? Guid.Empty,
                row.RuntimeTicks is long ticks && ticks > 0 ? TimeSpan.FromTicks(ticks) : null);
            catalog.Episodes[row.Id] = info;
            catalog.Episodes[row.JellyfinItemId] = info;
        }

        var seriesIds = catalog.Series.ToList();
        if (seriesIds.Count > 0)
        {
            var maxTicks = ShortEpisodeBlocks.MaxEpisodeDuration.Ticks;
            var seriesStats = await _db.Episodes.AsNoTracking()
                .Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId.Value) && e.RuntimeTicks > 0)
                .GroupBy(e => e.SeriesId!.Value)
                .Select(g => new
                {
                    SeriesId = g.Key,
                    Total = g.Count(),
                    Shorts = g.Count(e => e.RuntimeTicks < maxTicks)
                })
                .ToListAsync(cancellationToken);
            foreach (var row in seriesStats)
            {
                if (row.Total > 0 && row.Shorts * 2 >= row.Total)
                {
                    catalog.ShortSeries.Add(row.SeriesId);
                }
            }
        }

        return catalog;
    }

    private static string? KindFromSlotCandidates(LineupSlot slot, CatalogKinds catalog, ChannelContentType contentType)
    {
        var ordered = slot.Candidates.OrderBy(c => c.SortOrder).ToList();
        foreach (var candidate in ordered.Where(c => c.JellyfinItemId.HasValue))
        {
            var kind = KindFromItemId(candidate.JellyfinItemId, catalog, packedFromCandidate: true);
            if (kind is not null)
            {
                return kind;
            }
        }

        if (ordered.Count == 0)
        {
            return null;
        }

        return contentType switch
        {
            ChannelContentType.Movie => LineupSlotKinds.Movie,
            ChannelContentType.TvShow => LineupSlotKinds.TvShow,
            _ => null
        };
    }

    private static string? KindFromItemId(Guid? jellyfinItemId, CatalogKinds catalog, bool packedFromCandidate)
    {
        if (jellyfinItemId is not Guid id)
        {
            return null;
        }

        if (catalog.MusicVideos.Contains(id))
        {
            return null;
        }

        if (catalog.Movies.Contains(id))
        {
            return LineupSlotKinds.Movie;
        }

        if (catalog.Episodes.TryGetValue(id, out var episode))
        {
            if (packedFromCandidate && episode.Runtime is TimeSpan runtime && ShortEpisodeBlocks.IsShortRuntime(runtime))
            {
                return LineupSlotKinds.ShortBlock;
            }

            return LineupSlotKinds.TvShow;
        }

        if (catalog.Series.Contains(id))
        {
            return catalog.ShortSeries.Contains(id)
                ? LineupSlotKinds.ShortBlock
                : LineupSlotKinds.TvShow;
        }

        return null;
    }

    private static List<PlayoutItem> MergeSplitPrograms(List<PlayoutItem> items)
    {
        var merged = new List<PlayoutItem>();
        foreach (var item in items.OrderBy(i => i.Start))
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.JellyfinItemId.HasValue
                    && last.JellyfinItemId == item.JellyfinItemId
                    && item.Start <= last.Finish.AddMinutes(8))
                {
                    if (item.Finish > last.Finish)
                    {
                        last.Finish = item.Finish;
                    }

                    continue;
                }
            }

            merged.Add(item);
        }

        return merged;
    }

    private static TimeSpan Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private static DateTime LocalToUtc(DateOnly date, int minutesFromMidnight, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(
            date.ToDateTime(TimeOnly.MinValue).AddMinutes(minutesFromMidnight),
            DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), tz);
        }
    }

    private sealed class CatalogKinds
    {
        public HashSet<Guid> Movies { get; } = [];

        public HashSet<Guid> Series { get; } = [];

        public HashSet<Guid> ShortSeries { get; } = [];

        public HashSet<Guid> MusicVideos { get; } = [];

        public Dictionary<Guid, ShortEpisodeCatalogInfo> Episodes { get; } = [];
    }
}
