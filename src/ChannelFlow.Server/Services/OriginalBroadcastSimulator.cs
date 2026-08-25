using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// When Simulate original broadcasting is on, anniversary episodes and movies
/// (same month and day, any year) steal that night's primetime slots.
/// Sequential series order is not advanced.
/// </summary>
public sealed class OriginalBroadcastSimulator
{
    private const int MaxQueue = 16;

    private readonly FinTvDbContext _db;
    private readonly JellyfinCatalogService _catalog;
    private readonly ILibraryManager _library;

    public OriginalBroadcastSimulator(
        FinTvDbContext db,
        JellyfinCatalogService catalog,
        ILibraryManager library)
    {
        _db = db;
        _catalog = catalog;
        _library = library;
    }

    public static bool IsEnabled(Channel channel)
    {
        if (FinTvRuntime.Current?.Configuration.Ai.SimulateOriginalBroadcasting != true)
        {
            return false;
        }

        if (channel.ContentType is ChannelContentType.Weather
            or ChannelContentType.News
            or ChannelContentType.Music
            or ChannelContentType.MusicVideo)
        {
            return false;
        }

        return !PastTenseNewsCatalog.IsPastTenseNewsChannel(channel);
    }

    public async Task<Queue<AnniversaryPick>> BuildQueueAsync(
        Channel channel,
        DateOnly scheduleDate,
        IReadOnlyList<LineupSlot> daySlots,
        CancellationToken cancellationToken)
    {
        var lineupIds = CollectLineupItemIds(daySlots);
        var month = scheduleDate.Month;
        var day = scheduleDate.Day;
        var includeLeapDay = scheduleDate is { Month: 2, Day: 28 } && !DateTime.IsLeapYear(scheduleDate.Year);
        var catalogMode = JellyfinCatalogService.ResolveCatalogMode(channel);
        var includeEpisodes = catalogMode != ChannelCatalogMode.MovieOnly;
        var includeMovies = catalogMode is ChannelCatalogMode.MovieOnly or ChannelCatalogMode.Mixed
            || channel.ContentType == ChannelContentType.Movie;

        var ranked = new List<AnniversaryPick>();

        if (includeEpisodes)
        {
            var episodes = await _db.Episodes.AsNoTracking()
                .Where(e => !e.IsMissing && e.PremiereDate != null
                    && e.PremiereDate.Value.Month == month
                    && (e.PremiereDate.Value.Day == day || (includeLeapDay && e.PremiereDate.Value.Day == 29)))
                .Select(e => new { e.JellyfinItemId, e.SeriesId, e.PremiereDate, e.RuntimeTicks })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var group in episodes.GroupBy(e => e.SeriesId ?? e.JellyfinItemId))
            {
                var chosen = group.OrderByDescending(e => e.PremiereDate).First();
                ranked.Add(CreatePick(
                    chosen.JellyfinItemId,
                    chosen.PremiereDate,
                    chosen.RuntimeTicks,
                    lineupIds.Contains(chosen.SeriesId ?? Guid.Empty) || lineupIds.Contains(chosen.JellyfinItemId)));
            }
        }

        if (includeMovies)
        {
            var movies = await _db.Movies.AsNoTracking()
                .Where(m => !m.IsMissing && m.PremiereDate != null
                    && m.PremiereDate.Value.Month == month
                    && (m.PremiereDate.Value.Day == day || (includeLeapDay && m.PremiereDate.Value.Day == 29)))
                .Select(m => new { m.JellyfinItemId, m.PremiereDate, m.RuntimeTicks })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            ranked.AddRange(movies.Select(m => CreatePick(
                    m.JellyfinItemId,
                    m.PremiereDate,
                    m.RuntimeTicks,
                    lineupIds.Contains(m.JellyfinItemId))));
        }

        var queue = new Queue<AnniversaryPick>();
        foreach (var pick in ranked
            .OrderByDescending(p => p.OnLineup)
            .ThenByDescending(p => p.PremiereDate)
            .ThenBy(p => p.Duration)
            .Take(MaxQueue * 3))
        {
            var item = _library.GetItemById(pick.Id);
            if (item is null || item is Series)
            {
                continue;
            }

            if (!_catalog.IsPlayableOnChannel(item, channel, scheduleDate))
            {
                continue;
            }

            queue.Enqueue(pick with
            {
                Duration = item.RunTimeTicks is > 0
                    ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
                    : pick.Duration
            });
            if (queue.Count >= MaxQueue)
            {
                break;
            }
        }

        return queue;
    }

    public static AnniversaryPick? TryTakeFitting(
        Queue<AnniversaryPick> queue,
        int slotIndex,
        int primetimeEndSlot)
    {
        var remaining = primetimeEndSlot - slotIndex + 1;
        if (remaining <= 0)
        {
            return null;
        }

        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (SlotsForDuration(next.Duration) <= remaining)
            {
                return next;
            }
        }

        return null;
    }

    public static LineupSlot CreateSlot(int slotIndex, AnniversaryPick pick)
    {
        var span = SlotsForDuration(pick.Duration);
        return new LineupSlot
        {
            SlotIndex = slotIndex,
            SpanSlots = span,
            Candidates =
            [
                new SlotCandidate
                {
                    Kind = SlotCandidateKind.JellyfinItem,
                    JellyfinItemId = pick.Id,
                    Weight = 10,
                    SortOrder = 0
                }
            ]
        };
    }

    public static int SlotsForDuration(TimeSpan duration)
    {
        var minutes = duration > TimeSpan.Zero ? duration.TotalMinutes : 30;
        return Math.Clamp((int)Math.Ceiling(minutes / 30.0), 1, 8);
    }

    private static AnniversaryPick CreatePick(Guid id, DateTime? premiere, long? runtimeTicks, bool onLineup)
        => new()
        {
            Id = id,
            PremiereDate = premiere ?? DateTime.MinValue,
            Duration = runtimeTicks is > 0 ? TimeSpan.FromTicks(runtimeTicks.Value) : TimeSpan.FromMinutes(30),
            OnLineup = onLineup
        };

    private static HashSet<Guid> CollectLineupItemIds(IReadOnlyList<LineupSlot> slots)
    {
        var ids = new HashSet<Guid>();
        foreach (var slot in slots)
        {
            foreach (var candidate in slot.Candidates)
            {
                if (candidate.JellyfinItemId is Guid id && id != Guid.Empty)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}

public readonly record struct AnniversaryPick
{
    public Guid Id { get; init; }

    public DateTime PremiereDate { get; init; }

    public TimeSpan Duration { get; init; }

    public bool OnLineup { get; init; }
}
