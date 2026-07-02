using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class LineupService
{
    private readonly FinTvDbContext _db;

    public LineupService(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task<Lineup?> GetDefaultLineupAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        return await _db.Lineups
            .Include(l => l.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates.OrderBy(c => c.SortOrder))
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.IsDefault, cancellationToken);
    }

    public async Task<List<LineupOverride>> GetOverridesAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        return await _db.LineupOverrides
            .Include(o => o.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates.OrderBy(c => c.SortOrder))
            .Where(o => o.ChannelId == channelId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<LineupOverride?> GetOverrideAsync(Guid overrideId, CancellationToken cancellationToken = default)
    {
        return await _db.LineupOverrides
            .Include(o => o.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates)
            .FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);
    }

    public async Task UpdateDefaultSlotsAsync(Guid channelId, IReadOnlyList<LineupSlotDto> slots, CancellationToken cancellationToken = default)
    {
        var lineup = await _db.Lineups
            .Include(l => l.Slots)
                .ThenInclude(s => s.Candidates)
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.IsDefault, cancellationToken)
            ?? throw new InvalidOperationException("Default lineup not found.");

        ReplaceSlots(lineup.Slots, slots, lineupId: lineup.Id, overrideId: null);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LineupOverride> CreateOverrideAsync(Guid channelId, LineupOverrideDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new LineupOverride
        {
            ChannelId = channelId,
            Kind = dto.Kind,
            DayOfWeek = dto.DayOfWeek,
            SpecificDate = dto.SpecificDate,
            Name = dto.Name,
            Slots = dto.Slots.Select(s => MapSlot(s, null, null)).ToList()
        };

        foreach (var slot in entity.Slots)
        {
            slot.LineupOverrideId = entity.Id;
        }

        _db.LineupOverrides.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<LineupOverride?> UpdateOverrideAsync(Guid overrideId, LineupOverrideDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _db.LineupOverrides
            .Include(o => o.Slots)
                .ThenInclude(s => s.Candidates)
            .FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        entity.Kind = dto.Kind;
        entity.DayOfWeek = dto.DayOfWeek;
        entity.SpecificDate = dto.SpecificDate;
        entity.Name = dto.Name;

        ReplaceSlots(entity.Slots, dto.Slots, lineupId: null, overrideId: entity.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> DeleteOverrideAsync(Guid overrideId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.LineupOverrides.FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        _db.LineupOverrides.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<LineupSlot>> ResolveSlotsForDateAsync(Guid channelId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var overrides = await GetOverridesAsync(channelId, cancellationToken);
        var match = overrides.FirstOrDefault(o =>
            (o.Kind == LineupOverrideKind.SpecificDate && o.SpecificDate == date)
            || (o.Kind == LineupOverrideKind.DayOfWeek && o.DayOfWeek == date.DayOfWeek));

        if (match is not null)
        {
            return match.Slots.OrderBy(s => s.SlotIndex).ToList();
        }

        var defaultLineup = await GetDefaultLineupAsync(channelId, cancellationToken);
        return defaultLineup?.Slots.OrderBy(s => s.SlotIndex).ToList() ?? new List<LineupSlot>();
    }

    private void ReplaceSlots(ICollection<LineupSlot> existing, IReadOnlyList<LineupSlotDto> incoming, Guid? lineupId, Guid? overrideId)
    {
        _db.RemoveRange(existing);
        existing.Clear();

        foreach (var dto in incoming.OrderBy(s => s.SlotIndex))
        {
            var slot = MapSlot(dto, lineupId, overrideId);
            existing.Add(slot);
        }
    }

    private static LineupSlot MapSlot(LineupSlotDto dto, Guid? lineupId, Guid? overrideId)
    {
        return new LineupSlot
        {
            SlotIndex = dto.SlotIndex,
            SpanSlots = Math.Clamp(dto.SpanSlots, 1, 8),
            LineupId = lineupId,
            LineupOverrideId = overrideId,
            Candidates = dto.Candidates.Select(c => new SlotCandidate
            {
                Kind = c.Kind,
                JellyfinItemId = c.JellyfinItemId,
                CollectionName = c.CollectionName,
                FilterJson = c.FilterJson,
                Weight = c.Weight,
                SortOrder = c.SortOrder
            }).ToList()
        };
    }
}

public class LineupSlotDto
{
    public int SlotIndex { get; set; }

    public int SpanSlots { get; set; } = 1;

    public List<SlotCandidateDto> Candidates { get; set; } = new();
}

public class SlotCandidateDto
{
    public SlotCandidateKind Kind { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public string? CollectionName { get; set; }

    public string? FilterJson { get; set; }

    public int Weight { get; set; } = 1;

    public int SortOrder { get; set; }
}

public class LineupOverrideDto
{
    public LineupOverrideKind Kind { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }

    public DateOnly? SpecificDate { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<LineupSlotDto> Slots { get; set; } = ChannelService.CreateEmptySlots()
        .Select(s => new LineupSlotDto { SlotIndex = s.SlotIndex }).ToList();
}
