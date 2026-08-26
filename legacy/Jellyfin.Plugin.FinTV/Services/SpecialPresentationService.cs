using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class SpecialPresentationService
{
    private readonly FinTvDbContext _db;

    public SpecialPresentationService(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task<List<SpecialPresentation>> GetForChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        return await _db.SpecialPresentations
            .Include(p => p.Candidates.OrderBy(c => c.SortOrder))
            .Where(p => p.ChannelId == channelId)
            .OrderBy(p => p.DayOfWeek)
            .ThenBy(p => p.SlotIndex)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<SpecialPresentation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.SpecialPresentations
            .Include(p => p.Candidates.OrderBy(c => c.SortOrder))
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<SpecialPresentation> CreateAsync(Guid channelId, SpecialPresentationDto dto, CancellationToken cancellationToken = default)
    {
        await EnsureChannelAllowsPresentationsAsync(channelId, cancellationToken);
        ValidateDto(dto);
        await EnsureNoOverlapAsync(channelId, dto.DayOfWeek, dto.SlotIndex, dto.SpanSlots, excludeId: null, cancellationToken);

        var entity = MapEntity(channelId, dto);
        _db.SpecialPresentations.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<SpecialPresentation?> UpdateAsync(Guid id, SpecialPresentationDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _db.SpecialPresentations
            .Include(p => p.Candidates)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        ValidateDto(dto);
        await EnsureNoOverlapAsync(entity.ChannelId, dto.DayOfWeek, dto.SlotIndex, dto.SpanSlots, excludeId: id, cancellationToken);

        entity.Name = dto.Name.Trim();
        entity.Enabled = dto.Enabled;
        entity.DayOfWeek = dto.DayOfWeek;
        entity.SlotIndex = dto.SlotIndex;
        entity.SpanSlots = Math.Clamp(dto.SpanSlots, 1, 8);

        _db.SpecialPresentationCandidates.RemoveRange(entity.Candidates);
        entity.Candidates = MapCandidates(dto.Candidates);

        await _db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.SpecialPresentations.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        _db.SpecialPresentations.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public IReadOnlyList<LineupSlot> MergeIntoSlots(
        IReadOnlyList<LineupSlot> baseSlots,
        IReadOnlyList<SpecialPresentation> presentations,
        DayOfWeek dayOfWeek)
    {
        var slots = baseSlots.Select(CloneSlot).ToList();
        var slotMap = slots.ToDictionary(s => s.SlotIndex);

        foreach (var presentation in presentations.Where(p => p.Enabled && p.DayOfWeek == dayOfWeek))
        {
            var replacement = new LineupSlot
            {
                SlotIndex = presentation.SlotIndex,
                SpanSlots = Math.Clamp(presentation.SpanSlots, 1, 8),
                Candidates = presentation.Candidates.Select(c => new SlotCandidate
                {
                    Kind = c.Kind,
                    JellyfinItemId = c.JellyfinItemId,
                    CollectionName = c.CollectionName,
                    FilterJson = c.FilterJson,
                    FinTvListId = c.FinTvListId,
                    Weight = c.Weight,
                    SortOrder = c.SortOrder
                }).ToList()
            };

            slotMap[presentation.SlotIndex] = replacement;

            for (var i = 1; i < replacement.SpanSlots; i++)
            {
                var consumedIndex = presentation.SlotIndex + i;
                if (consumedIndex < 48)
                {
                    slotMap.Remove(consumedIndex);
                }
            }
        }

        return slotMap.Values.OrderBy(s => s.SlotIndex).ToList();
    }

    private static LineupSlot CloneSlot(LineupSlot slot)
    {
        return new LineupSlot
        {
            Id = slot.Id,
            SlotIndex = slot.SlotIndex,
            SpanSlots = slot.SpanSlots,
            Candidates = slot.Candidates.Select(c => new SlotCandidate
            {
                Kind = c.Kind,
                JellyfinItemId = c.JellyfinItemId,
                CollectionName = c.CollectionName,
                FilterJson = c.FilterJson,
                FinTvListId = c.FinTvListId,
                Weight = c.Weight,
                SortOrder = c.SortOrder
            }).ToList()
        };
    }

    private static SpecialPresentation MapEntity(Guid channelId, SpecialPresentationDto dto)
    {
        return new SpecialPresentation
        {
            ChannelId = channelId,
            Name = dto.Name.Trim(),
            Enabled = dto.Enabled,
            DayOfWeek = dto.DayOfWeek,
            SlotIndex = dto.SlotIndex,
            SpanSlots = Math.Clamp(dto.SpanSlots, 1, 8),
            Candidates = MapCandidates(dto.Candidates)
        };
    }

    private static List<SpecialPresentationCandidate> MapCandidates(IReadOnlyList<SlotCandidateDto> candidates)
    {
        return (candidates ?? new List<SlotCandidateDto>())
            .Select((c, index) => new SpecialPresentationCandidate
            {
                Kind = c.Kind,
                JellyfinItemId = c.JellyfinItemId,
                CollectionName = c.CollectionName,
                FilterJson = c.FilterJson,
                FinTvListId = c.FinTvListId,
                Weight = Math.Max(1, c.Weight),
                SortOrder = c.SortOrder > 0 ? c.SortOrder : index
            })
            .ToList();
    }

    private static void ValidateDto(SpecialPresentationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new InvalidOperationException("Presentation name is required.");
        }

        if (dto.SlotIndex is < 0 or > 47)
        {
            throw new InvalidOperationException("Slot index must be between 0 and 47.");
        }

        var span = Math.Clamp(dto.SpanSlots, 1, 8);
        if (dto.SlotIndex + span > 48)
        {
            throw new InvalidOperationException("Presentation span exceeds the end of the day.");
        }

        if (dto.Candidates is not { Count: > 0 })
        {
            throw new InvalidOperationException("At least one content candidate is required.");
        }
    }

    private async Task EnsureChannelAllowsPresentationsAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        if (channel.ContentType == ChannelContentType.Weather)
        {
            throw new InvalidOperationException("Special presentations are not supported on weather channels.");
        }
    }

    private async Task EnsureNoOverlapAsync(
        Guid channelId,
        DayOfWeek dayOfWeek,
        int slotIndex,
        int spanSlots,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var span = Math.Clamp(spanSlots, 1, 8);
        var start = slotIndex;
        var end = slotIndex + span - 1;

        var existing = await _db.SpecialPresentations
            .Where(p => p.ChannelId == channelId && p.Enabled && p.DayOfWeek == dayOfWeek)
            .Where(p => excludeId == null || p.Id != excludeId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var other in existing)
        {
            var otherStart = other.SlotIndex;
            var otherEnd = other.SlotIndex + Math.Clamp(other.SpanSlots, 1, 8) - 1;
            if (start <= otherEnd && end >= otherStart)
            {
                throw new InvalidOperationException($"Presentation overlaps with \"{other.Name}\" on {dayOfWeek}.");
            }
        }
    }
}

public class SpecialPresentationDto
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DayOfWeek DayOfWeek { get; set; }

    public int SlotIndex { get; set; }

    public int SpanSlots { get; set; } = 1;

    public List<SlotCandidateDto> Candidates { get; set; } = new();
}
