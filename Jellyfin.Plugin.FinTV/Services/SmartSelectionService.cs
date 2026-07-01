using System.Text.Json;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class PlayoutAnchorState
{
    public Dictionary<string, int> SeriesEpisodeIndex { get; set; } = new();

    public Dictionary<int, int> SlotShuffleCursor { get; set; } = new();

    public Dictionary<Guid, DateTime> LastAired { get; set; } = new();
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

        var historyCutoff = DateTime.UtcNow.AddDays(-(Plugin.Instance?.Configuration.HistoryDaysToConsider ?? 7));
        var recentIds = await _db.PlayoutHistory
            .Where(h => h.ChannelId == channel.Id && h.AiredAt >= historyCutoff)
            .Select(h => h.JellyfinItemId)
            .ToListAsync(cancellationToken);

        var resolved = new List<(SlotCandidate Candidate, ResolvedCandidate Item, double Score)>();

        foreach (var candidate in slot.Candidates.OrderBy(c => c.SortOrder))
        {
            var items = await ResolveCandidateAsync(channel, candidate, anchor, cancellationToken);
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
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
        return candidate.Kind switch
        {
            SlotCandidateKind.JellyfinItem when candidate.JellyfinItemId.HasValue =>
                await _catalog.ResolveItemAsync(candidate.JellyfinItemId.Value, channel, cancellationToken),
            SlotCandidateKind.Collection when !string.IsNullOrWhiteSpace(candidate.CollectionName) =>
                await _catalog.ResolveCollectionAsync(candidate.CollectionName, channel, anchor, cancellationToken),
            SlotCandidateKind.FilterQuery when !string.IsNullOrWhiteSpace(candidate.FilterJson) =>
                await _catalog.ResolveFilterAsync(candidate.FilterJson, channel, anchor, cancellationToken),
            _ => Array.Empty<ResolvedCandidate>()
        };
    }
}

public class ResolvedCandidate
{
    public Guid? JellyfinItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public bool IsVirtual { get; set; }

    public VirtualContentSource VirtualSource { get; set; }
}
