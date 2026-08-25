using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public class LineupService
{
    private readonly FinTvDbContext _db;
    private readonly SpecialPresentationService _specialPresentations;

    public LineupService(FinTvDbContext db, SpecialPresentationService specialPresentations)
    {
        _db = db;
        _specialPresentations = specialPresentations;
    }

    public static LineupSlotDto CreateWeatherSlotDto()
    {
        return new LineupSlotDto
        {
            SlotIndex = 0,
            SpanSlots = 48,
            Candidates = new List<SlotCandidateDto>()
        };
    }

    public async Task<Lineup?> GetDefaultLineupAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var lineup = await _db.Lineups
            .AsNoTracking()
            .AsSplitQuery()
            .Include(l => l.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates.OrderBy(c => c.SortOrder))
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.IsDefault, cancellationToken);
        if (lineup is not null)
        {
            lineup.Slots = await ApplyRuntimeSpansAsync(lineup.Slots, cancellationToken);
        }

        return lineup;
    }

    public async Task<List<LineupOverride>> GetOverridesAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var overrides = await _db.LineupOverrides
            .AsSplitQuery()
            .Include(o => o.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates.OrderBy(c => c.SortOrder))
            .Where(o => o.ChannelId == channelId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var overrideRuntimes = await LoadRuntimeMinutesAsync(
            overrides.SelectMany(o => o.Slots),
            cancellationToken);
        foreach (var item in overrides)
        {
            item.Slots = LineupSlotSpans.ExpandUsingRuntimes(item.Slots, overrideRuntimes);
        }

        return overrides;
    }

    public async Task<LineupOverride?> GetOverrideAsync(Guid overrideId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.LineupOverrides
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates)
            .FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);
        if (entity is not null)
        {
            entity.Slots = await ApplyRuntimeSpansAsync(entity.Slots, cancellationToken);
        }

        return entity;
    }

    public async Task UpdateDefaultSlotsAsync(Guid channelId, IReadOnlyList<LineupSlotDto> slots, CancellationToken cancellationToken = default)
    {
        var lineup = await _db.Lineups
            .Include(l => l.Slots)
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.IsDefault, cancellationToken)
            ?? throw new InvalidOperationException("Default lineup not found.");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await ReplaceSlotsAsync(lineup.Slots, slots, lineupId: lineup.Id, overrideId: null, cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                _db.ChangeTracker.Clear();
                lineup = await _db.Lineups
                    .Include(l => l.Slots)
                    .FirstOrDefaultAsync(l => l.Id == lineup.Id, cancellationToken)
                    ?? throw new InvalidOperationException("Default lineup not found.");
            }
        }
    }

    public async Task ReplaceWeeklyDayLineupsAsync(
        Guid channelId,
        IReadOnlyDictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        CancellationToken cancellationToken = default)
    {
        await EnsureNotWeatherChannelAsync(channelId, cancellationToken);

        var existing = await _db.LineupOverrides
            .Where(o => o.ChannelId == channelId && o.Kind == LineupOverrideKind.DayOfWeek)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _db.LineupOverrides.RemoveRange(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        foreach (var (day, slots) in weekly.OrderBy(kv => kv.Key))
        {
            var entity = new LineupOverride
            {
                ChannelId = channelId,
                Kind = LineupOverrideKind.DayOfWeek,
                DayOfWeek = day,
                Name = day.ToString(),
                Slots = LineupSlotSpans.ExpandUsingRuntimes(
                        slots,
                        await LoadRuntimeMinutesAsync(slots, cancellationToken))
                    .Select(s => MapSlot(s, null, null))
                    .ToList()
            };
            foreach (var slot in entity.Slots)
            {
                slot.LineupOverrideId = entity.Id;
            }

            _db.LineupOverrides.Add(entity);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LineupOverride> CreateOverrideAsync(Guid channelId, LineupOverrideDto dto, CancellationToken cancellationToken = default)
    {
        await EnsureNotWeatherChannelAsync(channelId, cancellationToken);

        var entity = new LineupOverride
        {
            ChannelId = channelId,
            Kind = dto.Kind,
            DayOfWeek = dto.DayOfWeek,
            SpecificDate = dto.SpecificDate,
            Name = dto.Name,
            Slots = LineupSlotSpans.ExpandUsingRuntimes(
                    dto.Slots,
                    await LoadRuntimeMinutesAsync(dto.Slots, cancellationToken))
                .Select(s => MapSlot(s, null, null))
                .ToList()
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await ReplaceSlotsAsync(entity.Slots, dto.Slots, lineupId: null, overrideId: entity.Id, cancellationToken);
                return entity;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                _db.ChangeTracker.Clear();
                entity = await _db.LineupOverrides
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
            }
        }

        return entity;
    }

    private async Task ReplaceSlotsAsync(
        ICollection<LineupSlot> existingCollection,
        IReadOnlyList<LineupSlotDto> incoming,
        Guid? lineupId,
        Guid? overrideId,
        CancellationToken cancellationToken)
    {
        if (lineupId.HasValue)
        {
            var slotIds = await _db.LineupSlots
                .Where(s => s.LineupId == lineupId.Value)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            if (slotIds.Count > 0)
            {
                await _db.SlotCandidates
                    .Where(c => slotIds.Contains(c.LineupSlotId))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await _db.LineupSlots
                .Where(s => s.LineupId == lineupId.Value)
                .ExecuteDeleteAsync(cancellationToken);

            DetachTrackedLineupSlots(lineupId: lineupId.Value, overrideId: null);
        }
        else if (overrideId.HasValue)
        {
            var slotIds = await _db.LineupSlots
                .Where(s => s.LineupOverrideId == overrideId.Value)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            if (slotIds.Count > 0)
            {
                await _db.SlotCandidates
                    .Where(c => slotIds.Contains(c.LineupSlotId))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await _db.LineupSlots
                .Where(s => s.LineupOverrideId == overrideId.Value)
                .ExecuteDeleteAsync(cancellationToken);

            DetachTrackedLineupSlots(lineupId: null, overrideId: overrideId.Value);
        }

        existingCollection.Clear();

        var runtimes = await LoadRuntimeMinutesAsync(incoming, cancellationToken);
        foreach (var dto in LineupSlotSpans.ExpandUsingRuntimes(incoming, runtimes))
        {
            var slot = MapSlot(dto, lineupId, overrideId);
            existingCollection.Add(slot);
            _db.LineupSlots.Add(slot);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private void DetachTrackedLineupSlots(Guid? lineupId, Guid? overrideId)
    {
        foreach (var entry in _db.ChangeTracker.Entries<LineupSlot>()
            .Where(entry => lineupId.HasValue
                ? entry.Entity.LineupId == lineupId.Value
                : entry.Entity.LineupOverrideId == overrideId)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in _db.ChangeTracker.Entries<SlotCandidate>().ToList())
        {
            entry.State = EntityState.Detached;
        }
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

    public async Task<LineupResolutionSnapshot> LoadResolutionSnapshotAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var live = await _db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => (bool?)(c.ContentType == ChannelContentType.Weather || c.ContentType == ChannelContentType.News))
            .FirstOrDefaultAsync(cancellationToken);

        if (live is null)
        {
            return LineupResolutionSnapshot.Empty;
        }

        if (live.Value)
        {
            return LineupResolutionSnapshot.ContinuousLive;
        }

        var overrides = await GetOverridesAsync(channelId, cancellationToken);
        var defaultLineup = await _db.Lineups
            .AsNoTracking()
            .AsSplitQuery()
            .Include(l => l.Slots.OrderBy(s => s.SlotIndex))
                .ThenInclude(s => s.Candidates.OrderBy(c => c.SortOrder))
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.IsDefault, cancellationToken);

        var presentations = await _specialPresentations.GetForChannelAsync(channelId, cancellationToken);
        var runtimeIds = CandidateIds(defaultLineup?.Slots)
            .Concat(overrides.SelectMany(o => CandidateIds(o.Slots)))
            .Concat(presentations.SelectMany(p => p.Candidates.Select(c => c.JellyfinItemId)));
        var runtimes = await LoadRuntimeMinutesAsync(runtimeIds, cancellationToken);
        if (defaultLineup is not null)
        {
            defaultLineup.Slots = LineupSlotSpans.ExpandUsingRuntimes(defaultLineup.Slots, runtimes);
        }

        return new LineupResolutionSnapshot(false, defaultLineup, overrides, presentations, runtimes);
    }

    public IReadOnlyList<LineupSlot> ResolveSlotsForDate(LineupResolutionSnapshot snapshot, DateOnly date)
    {
        if (snapshot.IsContinuousLive)
        {
            return WeatherLineupHelper.CreateDailySlots();
        }

        var match = snapshot.Overrides.FirstOrDefault(o =>
            (o.Kind == LineupOverrideKind.SpecificDate && o.SpecificDate == date)
            || (o.Kind == LineupOverrideKind.DayOfWeek && o.DayOfWeek == date.DayOfWeek));

        IReadOnlyList<LineupSlot> baseSlots = match is not null
            ? match.Slots.OrderBy(s => s.SlotIndex).ToList()
            : snapshot.DefaultLineup?.Slots.OrderBy(s => s.SlotIndex).ToList() ?? new List<LineupSlot>();

        return LineupSlotSpans.ExpandUsingRuntimes(
            _specialPresentations.MergeIntoSlots(baseSlots, snapshot.Presentations, date.DayOfWeek),
            snapshot.ItemRuntimeMinutes);
    }

    public async Task<IReadOnlyList<LineupSlot>> ResolveSlotsForDateAsync(
        Guid channelId,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadResolutionSnapshotAsync(channelId, cancellationToken);
        return ResolveSlotsForDate(snapshot, date);
    }

    private async Task EnsureNotWeatherChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel?.IsContinuousLive == true)
        {
            throw new InvalidOperationException("Lineup overrides are not supported on live weather or news channels.");
        }
    }

    private async Task<ICollection<LineupSlot>> ApplyRuntimeSpansAsync(
        ICollection<LineupSlot> slots,
        CancellationToken cancellationToken)
    {
        var runtimes = await LoadRuntimeMinutesAsync(slots, cancellationToken);
        return LineupSlotSpans.ExpandUsingRuntimes(slots, runtimes);
    }

    private Task<Dictionary<Guid, int>> LoadRuntimeMinutesAsync(
        IEnumerable<LineupSlot> slots,
        CancellationToken cancellationToken)
        => LoadRuntimeMinutesAsync(CandidateIds(slots), cancellationToken);

    private Task<Dictionary<Guid, int>> LoadRuntimeMinutesAsync(
        IEnumerable<LineupSlotDto> slots,
        CancellationToken cancellationToken)
        => LoadRuntimeMinutesAsync(CandidateIds(slots), cancellationToken);

    private async Task<Dictionary<Guid, int>> LoadRuntimeMinutesAsync(
        IEnumerable<Guid?> ids,
        CancellationToken cancellationToken)
    {
        var idList = ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var result = new Dictionary<Guid, int>();
        if (idList.Count == 0)
        {
            return result;
        }

        var movies = await _db.Movies.AsNoTracking()
            .Where(m => idList.Contains(m.Id) || idList.Contains(m.JellyfinItemId))
            .Select(m => new { m.Id, m.JellyfinItemId, m.RuntimeTicks })
            .ToListAsync(cancellationToken);
        foreach (var movie in movies)
        {
            AddRuntime(result, movie.Id, movie.JellyfinItemId, movie.RuntimeTicks);
        }

        var episodes = await _db.Episodes.AsNoTracking()
            .Where(e => idList.Contains(e.Id) || idList.Contains(e.JellyfinItemId))
            .Select(e => new { e.Id, e.JellyfinItemId, e.RuntimeTicks })
            .ToListAsync(cancellationToken);
        foreach (var episode in episodes)
        {
            AddRuntime(result, episode.Id, episode.JellyfinItemId, episode.RuntimeTicks);
        }

        return result;
    }

    private static IEnumerable<Guid?> CandidateIds(IEnumerable<LineupSlot>? slots)
        => (slots ?? []).SelectMany(s => s.Candidates).Select(c => c.JellyfinItemId);

    private static IEnumerable<Guid?> CandidateIds(IEnumerable<LineupSlotDto>? slots)
        => (slots ?? []).SelectMany(s => s.Candidates ?? []).Select(c => c.JellyfinItemId);

    private static void AddRuntime(Dictionary<Guid, int> result, Guid id, Guid jellyfinItemId, long? ticks)
    {
        if (ticks is not > 0)
        {
            return;
        }

        var minutes = (int)Math.Round(TimeSpan.FromTicks(ticks.Value).TotalMinutes);
        if (minutes <= LineupSlotSpans.MinutesPerSlot)
        {
            return;
        }

        result[id] = minutes;
        result[jellyfinItemId] = minutes;
    }

    private static LineupSlot MapSlot(LineupSlotDto dto, Guid? lineupId, Guid? overrideId)
    {
        return new LineupSlot
        {
            SlotIndex = dto.SlotIndex,
            SpanSlots = Math.Clamp(dto.SpanSlots, 1, 8),
            IsRerunSlot = dto.IsRerunSlot,
            LineupId = lineupId,
            LineupOverrideId = overrideId,
            Candidates = (dto.Candidates ?? new List<SlotCandidateDto>()).Select(c => new SlotCandidate
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
}

public sealed class LineupResolutionSnapshot
{
    public static readonly LineupResolutionSnapshot Empty = new(false, null, [], []);

    public static readonly LineupResolutionSnapshot ContinuousLive = new(true, null, [], []);

    public LineupResolutionSnapshot(
        bool isContinuousLive,
        Lineup? defaultLineup,
        IReadOnlyList<LineupOverride> overrides,
        IReadOnlyList<SpecialPresentation> presentations,
        IReadOnlyDictionary<Guid, int>? itemRuntimeMinutes = null)
    {
        IsContinuousLive = isContinuousLive;
        DefaultLineup = defaultLineup;
        Overrides = overrides;
        Presentations = presentations;
        ItemRuntimeMinutes = itemRuntimeMinutes ?? new Dictionary<Guid, int>();
    }

    public bool IsContinuousLive { get; }

    public Lineup? DefaultLineup { get; }

    public IReadOnlyList<LineupOverride> Overrides { get; }

    public IReadOnlyList<SpecialPresentation> Presentations { get; }

    public IReadOnlyDictionary<Guid, int> ItemRuntimeMinutes { get; }
}

public class LineupSlotDto
{
    public int SlotIndex { get; set; }

    public int SpanSlots { get; set; } = 1;

    public bool IsRerunSlot { get; set; }

    public List<SlotCandidateDto> Candidates { get; set; } = new();
}

public class SlotCandidateDto
{
    public SlotCandidateKind Kind { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public string? CollectionName { get; set; }

    public string? FilterJson { get; set; }

    public Guid? FinTvListId { get; set; }

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
