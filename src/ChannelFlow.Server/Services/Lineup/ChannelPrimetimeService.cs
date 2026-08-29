using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// User-assigned 6:00–9:00pm series for prime-TV channels.
/// </summary>
public class ChannelPrimetimeService
{
    private readonly FinTvDbContext _db;
    private readonly JellyfinCatalogService _catalog;

    public ChannelPrimetimeService(FinTvDbContext db, JellyfinCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<ChannelPrimetimeSlot>> LoadAsync(Guid channelId, CancellationToken cancellationToken)
    {
        return await _db.ChannelPrimetimeSlots
            .AsNoTracking()
            .Include(s => s.Candidates)
            .Where(s => s.ChannelId == channelId)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<object>> ListEligibleShowsAsync(Channel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = _catalog.BrowseForAiManifest(channel, ChannelCatalogMode.TvOnly, 1000);
        return items
            .OfType<Series>()
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .OrderBy(s => s.SortName ?? s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => (object)new
            {
                id = s.Id,
                title = s.Name,
                year = s.ProductionYear
            })
            .ToList();
    }

    public object BuildResponse(IReadOnlyList<ChannelPrimetimeSlot> slots)
    {
        var byIndex = slots.ToDictionary(s => s.SlotIndex);
        var result = new List<object>();
        for (var index = AiPlayoutTemplates.PrimeTimeStartSlot;
             index <= AiPlayoutTemplates.AssignedPrimeTimeEndSlot;
             index++)
        {
            byIndex.TryGetValue(index, out var slot);
            var candidates = (slot?.Candidates ?? [])
                .OrderBy(c => c.SortOrder)
                .Select(c => new { id = c.SeriesId, title = c.Title })
                .ToList();
            result.Add(new
            {
                slotIndex = index,
                label = FormatSlotLabel(index),
                candidates
            });
        }

        return new { slots = result };
    }

    public async Task SaveAsync(
        Guid channelId,
        IReadOnlyList<ChannelPrimetimeSlotRequest> slots,
        CancellationToken cancellationToken)
    {
        var existing = await _db.ChannelPrimetimeSlots
            .Include(s => s.Candidates)
            .Where(s => s.ChannelId == channelId)
            .ToListAsync(cancellationToken);
        _db.ChannelPrimetimeSlots.RemoveRange(existing);

        foreach (var incoming in slots)
        {
            if (!AiPlayoutTemplates.IsAssignedPrimetimeSlot(incoming.SlotIndex))
            {
                continue;
            }

            var candidates = (incoming.Candidates ?? [])
                .Select(c => new
                {
                    SeriesId = c.SeriesId != Guid.Empty ? c.SeriesId : c.Id,
                    c.Title
                })
                .Where(c => c.SeriesId != Guid.Empty)
                .GroupBy(c => c.SeriesId)
                .Select((g, i) => new ChannelPrimetimeCandidate
                {
                    SeriesId = g.Key,
                    Title = string.IsNullOrWhiteSpace(g.First().Title) ? "Show" : g.First().Title!.Trim(),
                    SortOrder = i
                })
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            _db.ChannelPrimetimeSlots.Add(new ChannelPrimetimeSlot
            {
                ChannelId = channelId,
                SlotIndex = incoming.SlotIndex,
                Candidates = candidates
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public static HashSet<Guid> ExclusiveSeriesIds(IEnumerable<ChannelPrimetimeSlot> slots)
        => slots
            .SelectMany(s => s.Candidates)
            .Select(c => c.SeriesId)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

    public static LineupSlot? CreateLineupSlot(IEnumerable<ChannelPrimetimeSlot> slots, int slotIndex)
    {
        var match = slots.FirstOrDefault(s => s.SlotIndex == slotIndex);
        if (match is null || match.Candidates.Count == 0)
        {
            return null;
        }

        return new LineupSlot
        {
            SlotIndex = slotIndex,
            SpanSlots = 1,
            Candidates = match.Candidates
                .OrderBy(c => c.SortOrder)
                .Select((c, i) => new SlotCandidate
                {
                    Kind = SlotCandidateKind.JellyfinItem,
                    JellyfinItemId = c.SeriesId,
                    Weight = 1,
                    SortOrder = i
                })
                .ToList()
        };
    }

    public static void StampWeekly(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<ChannelPrimetimeSlot> assignments)
    {
        foreach (var daySlots in weekly.Values)
        {
            for (var i = AiPlayoutTemplates.EarlyBirdStartSlot; i <= AiPlayoutTemplates.EarlyBirdEndSlot; i++)
            {
                daySlots.RemoveAll(s => s.SlotIndex == i);
                daySlots.Add(new LineupSlotDto
                {
                    SlotIndex = i,
                    SpanSlots = 1,
                    IsRerunSlot = true,
                    Candidates = []
                });
            }

            foreach (var assignment in assignments)
            {
                if (!AiPlayoutTemplates.IsAssignedPrimetimeSlot(assignment.SlotIndex)
                    || assignment.Candidates.Count == 0)
                {
                    continue;
                }

                TrimOverlappingSpans(daySlots, assignment.SlotIndex);
                daySlots.RemoveAll(s => s.SlotIndex == assignment.SlotIndex);
                daySlots.Add(new LineupSlotDto
                {
                    SlotIndex = assignment.SlotIndex,
                    SpanSlots = 1,
                    IsRerunSlot = false,
                    Candidates = assignment.Candidates
                        .OrderBy(c => c.SortOrder)
                        .Select((c, i) => new SlotCandidateDto
                        {
                            Kind = SlotCandidateKind.JellyfinItem,
                            JellyfinItemId = c.SeriesId,
                            Weight = 1,
                            SortOrder = i
                        })
                        .ToList()
                });
            }

            daySlots.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
        }
    }

    private static void TrimOverlappingSpans(List<LineupSlotDto> slots, int slotIndex)
    {
        foreach (var slot in slots.Where(s => s.SlotIndex < slotIndex && s.SlotIndex + s.SpanSlots > slotIndex).ToList())
        {
            slot.SpanSlots = Math.Max(1, slotIndex - slot.SlotIndex);
        }
    }

    public static string FormatSlotLabel(int slotIndex)
    {
        var minutes = slotIndex * 30;
        var hour = minutes / 60;
        var minute = minutes % 60;
        var period = hour >= 12 ? "PM" : "AM";
        var display = hour % 12;
        if (display == 0)
        {
            display = 12;
        }

        return $"{display}:{minute:00} {period}";
    }
}

public class ChannelPrimetimeSlotRequest
{
    public int SlotIndex { get; set; }

    public List<ChannelPrimetimeCandidateRequest>? Candidates { get; set; }
}

public class ChannelPrimetimeCandidateRequest
{
    public Guid SeriesId { get; set; }

    public Guid Id { get; set; }

    public string? Title { get; set; }
}
