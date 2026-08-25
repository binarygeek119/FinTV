using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public class PlayoutAnchorState
{
    public Dictionary<string, int> SeriesEpisodeIndex { get; set; } = new();

    public Dictionary<int, int> SlotShuffleCursor { get; set; } = new();

    public Dictionary<Guid, int> ListCursor { get; set; } = new();

    public Dictionary<Guid, DateTime> LastAired { get; set; } = new();

    public string? LastHolidayId { get; set; }

    /// <summary>
    /// Recently played music-video artists, oldest first. Used to avoid back-to-back repeats
    /// and to maximize spacing before the same artist returns.
    /// </summary>
    public List<string> RecentMusicVideoArtists { get; set; } = new();
}

public class SmartSelectionService
{
    private readonly FinTvDbContext _db;
    private readonly JellyfinCatalogService _catalog;

    public SmartSelectionService(FinTvDbContext db, JellyfinCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<ResolvedCandidate?> PickCandidateAsync(
        Channel channel,
        LineupSlot slot,
        DateOnly scheduleDate,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken = default)
    {
        if (slot.Candidates.Count == 0)
        {
            return null;
        }

        var historyCutoff = DateTime.UtcNow.AddDays(-(FinTvRuntime.Current?.Configuration.HistoryDaysToConsider ?? 7));
        var recentIds = await _db.PlayoutHistory
            .Where(h => h.ChannelId == channel.Id && h.AiredAt >= historyCutoff)
            .Select(h => h.JellyfinItemId)
            .ToListAsync(cancellationToken);

        var resolved = new List<(SlotCandidate Candidate, ResolvedCandidate Item, double Score)>();

        foreach (var candidate in slot.Candidates.OrderBy(c => c.SortOrder))
        {
            var items = await ResolveCandidateAsync(channel, candidate, scheduleDate, anchor, slot.SlotIndex, cancellationToken);
            foreach (var item in items)
            {
                var score = ComputeScore(item, candidate.Weight, recentIds, anchor);
                resolved.Add((candidate, item, score));
            }
        }

        if (resolved.Count == 0)
        {
            return null;
        }

        var rng = CreateRng(channel.PlayoutSeed, scheduleDate, slot.SlotIndex);
        var maxScore = resolved.Max(r => r.Score);
        var top = resolved.Where(r => r.Score >= maxScore - 0.001).ToList();
        var pick = top[rng.Next(top.Count)].Item;
        anchor.LastAired[pick.JellyfinItemId ?? Guid.Empty] = scheduleDate.ToDateTime(TimeOnly.MinValue);
        return pick;
    }

    /// <summary>
    /// Next short episode of the same series, skipping items already used in this timeslot.
    /// </summary>
    public async Task<ResolvedCandidate?> PickNextSeriesEpisodeAsync(
        Channel channel,
        Guid seriesId,
        DateOnly scheduleDate,
        PlayoutAnchorState anchor,
        ISet<Guid> excludeItemIds,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var resolved = await _catalog.ResolveItemAsync(
                seriesId,
                channel,
                anchor,
                scheduleDate,
                cancellationToken);
            var pick = resolved.FirstOrDefault();
            if (pick is null)
            {
                return null;
            }

            if (pick.JellyfinItemId is Guid id && excludeItemIds.Contains(id))
            {
                continue;
            }

            if (!ShortEpisodeBlocks.IsShortRuntime(pick.Duration))
            {
                return null;
            }

            if (pick.JellyfinItemId.HasValue)
            {
                anchor.LastAired[pick.JellyfinItemId.Value] = scheduleDate.ToDateTime(TimeOnly.MinValue);
            }

            return pick;
        }

        return null;
    }

    private static double ComputeScore(ResolvedCandidate item, int weight, List<Guid?> recentIds, PlayoutAnchorState anchor)
    {
        var score = weight * 10.0;
        if (item.JellyfinItemId.HasValue && recentIds.Contains(item.JellyfinItemId))
        {
            score -= 50;
        }

        if (item.JellyfinItemId.HasValue
            && anchor.LastAired.TryGetValue(item.JellyfinItemId.Value, out var last))
        {
            score -= (DateTime.UtcNow - last).TotalDays;
        }

        return score;
    }

    private static Random CreateRng(int seed, DateOnly date, int slotIndex)
    {
        var combined = HashCode.Combine(seed, date.DayNumber, slotIndex);
        return new Random(combined);
    }

    private async Task<IReadOnlyList<ResolvedCandidate>> ResolveCandidateAsync(
        Channel channel,
        SlotCandidate candidate,
        DateOnly scheduleDate,
        PlayoutAnchorState anchor,
        int slotIndex,
        CancellationToken cancellationToken)
    {
        return candidate.Kind switch
        {
            SlotCandidateKind.JellyfinItem when candidate.JellyfinItemId.HasValue =>
                await _catalog.ResolveItemAsync(candidate.JellyfinItemId.Value, channel, anchor, scheduleDate, cancellationToken),
            SlotCandidateKind.Collection when !string.IsNullOrWhiteSpace(candidate.CollectionName) =>
                await _catalog.ResolveCollectionAsync(candidate.CollectionName, channel, anchor, scheduleDate, cancellationToken),
            SlotCandidateKind.FilterQuery when !string.IsNullOrWhiteSpace(candidate.FilterJson) =>
                await _catalog.ResolveFilterAsync(candidate.FilterJson, channel, anchor, scheduleDate, cancellationToken),
            SlotCandidateKind.Playlist when candidate.FinTvListId.HasValue =>
                await _catalog.ResolvePlaylistAsync(candidate.FinTvListId.Value, channel, anchor, scheduleDate, slotIndex, cancellationToken),
            _ => Array.Empty<ResolvedCandidate>()
        };
    }
}

public class ResolvedCandidate
{
    public Guid? JellyfinItemId { get; set; }

    public Guid? SeriesId { get; set; }

    public string Title { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public bool IsVirtual { get; set; }

    public VirtualContentSource VirtualSource { get; set; }
}
