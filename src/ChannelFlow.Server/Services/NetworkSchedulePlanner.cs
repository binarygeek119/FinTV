using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Expands an AI weekly programming grid into sticky 7-day lineups.
/// Day 8–14 reuse the same weekday templates while episode order continues in playout.
/// </summary>
public static class NetworkSchedulePlanner
{
    public const int OvernightRerunStartSlot = 4;
    public const int OvernightRerunEndSlot = 11;

    private static readonly DayOfWeek[] Weekdays =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
    ];

    public static Dictionary<DayOfWeek, List<LineupSlotDto>> CloneDailyToWeek(IReadOnlyList<LineupSlotDto> daily)
    {
        var copy = CloneSlots(daily);
        var weekly = new Dictionary<DayOfWeek, List<LineupSlotDto>>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            weekly[day] = CloneSlots(copy);
        }

        return weekly;
    }

    internal static Dictionary<DayOfWeek, List<LineupSlotDto>> ExpandBlocks(
        IReadOnlyList<AiGeneratedBlock> blocks,
        IReadOnlyList<AiCatalogEntry> catalog,
        ChannelCatalogMode catalogMode,
        ChannelContentType contentType)
    {
        var catalogById = catalog.ToDictionary(c => c.Id);
        var catalogByN = catalog
            .Select((c, index) => (c, n: index + 1))
            .ToDictionary(x => x.n, x => x.c);
        var weekly = new Dictionary<DayOfWeek, List<LineupSlotDto>>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            weekly[day] = CreateEmptySlotDtos();
        }

        foreach (var block in blocks)
        {
            var days = ParseDays(block.Days);
            var start = Math.Clamp(block.StartSlot, 0, 47);
            if (IsRerunBlock(block))
            {
                var rerunLength = Math.Clamp(block.SpanSlots ?? block.EpisodeBlock ?? 1, 1, 48 - start);
                foreach (var day in days)
                {
                    PlaceRerun(weekly[day], start, rerunLength);
                }

                continue;
            }

            var entry = ResolveEntry(block, catalogById, catalogByN);
            if (entry is null)
            {
                continue;
            }

            var isMovie = string.Equals(block.Kind, "movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Type, "Movie", StringComparison.OrdinalIgnoreCase);
            var length = isMovie
                ? Math.Clamp(block.SpanSlots ?? Math.Max(1, (int)Math.Ceiling(Math.Max(30, entry.RuntimeMinutes) / 30.0)), 1, 8)
                : Math.Clamp(block.EpisodeBlock ?? block.SpanSlots ?? 2, 1, 6);

            foreach (var day in days)
            {
                PlaceSeriesOrMovie(weekly[day], entry.Id, start, length, isMovie);
            }
        }

        foreach (var day in weekly.Keys.ToList())
        {
            FillRemainingGaps(weekly[day], catalog, catalogMode, contentType);
        }

        return weekly;
    }

    public static void SprinkleMovies(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<AiCatalogEntry> catalog,
        ChannelCatalogMode catalogMode)
    {
        if (catalogMode != ChannelCatalogMode.Mixed)
        {
            return;
        }

        var movies = catalog
            .Where(c => string.Equals(c.Type, "Movie", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Year ?? int.MaxValue)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (movies.Count == 0)
        {
            return;
        }

        TryPlaceMovie(weekly, DayOfWeek.Friday, 40, movies, 0);
        if (movies.Count > 1)
        {
            TryPlaceMovie(weekly, DayOfWeek.Saturday, 36, movies, 1);
        }
    }

    public static List<LineupSlotDto> CreateFilterSlots(string? filterJson)
    {
        var slots = CreateEmptySlotDtos();
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return slots;
        }

        foreach (var slot in slots)
        {
            slot.Candidates =
            [
                new SlotCandidateDto
                {
                    Kind = SlotCandidateKind.FilterQuery,
                    FilterJson = filterJson,
                    Weight = 1,
                    SortOrder = 0
                }
            ];
        }

        return slots;
    }

    public static List<LineupSlotDto> CreateEmptySlotDtos()
        => Enumerable.Range(0, 48)
            .Select(i => new LineupSlotDto { SlotIndex = i, SpanSlots = 1 })
            .ToList();

    public static bool IsOvernightRerunSlot(int slotIndex)
        => slotIndex >= OvernightRerunStartSlot && slotIndex <= OvernightRerunEndSlot;

    public static void ApplyTemplateRerunDayparts(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        AiPlayoutTemplate? template)
    {
        if (template?.Dayparts is not { Count: > 0 } dayparts)
        {
            return;
        }

        foreach (var daypart in dayparts.Where(IsRerunDaypart))
        {
            foreach (var slots in weekly.Values)
            {
                for (var i = 0; i < 48; i++)
                {
                    if (daypart.ContainsSlot(i))
                    {
                        PlaceRerun(slots, i, 1);
                    }
                }
            }
        }
    }

    private static bool IsRerunDaypart(AiPlayoutDaypart daypart)
        => daypart.Name.Contains("rerun", StringComparison.OrdinalIgnoreCase);

    private static bool IsRerunBlock(AiGeneratedBlock block)
        => string.Equals(block.Kind, "rerun", StringComparison.OrdinalIgnoreCase);

    public static void FillRemainingGaps(
        List<LineupSlotDto> slots,
        IReadOnlyList<AiCatalogEntry> catalog,
        ChannelCatalogMode catalogMode,
        ChannelContentType contentType)
    {
        var occupied = new bool[48];
        foreach (var slot in slots.Where(s => s.IsRerunSlot || s.Candidates.Count > 0))
        {
            for (var i = slot.SlotIndex; i < slot.SlotIndex + slot.SpanSlots && i < 48; i++)
            {
                occupied[i] = true;
            }
        }

        var seriesFirst = catalogMode is ChannelCatalogMode.TvOnly or ChannelCatalogMode.Mixed
            && contentType == ChannelContentType.TvShow;
        var fillQueue = catalog
            .OrderBy(e => seriesFirst && e.Type is "Movie" or "Clip" ? 1 : 0)
            .ThenBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Id)
            .ToList();
        if (fillQueue.Count == 0)
        {
            return;
        }

        var q = 0;
        for (var i = 0; i < 48; i++)
        {
            if (occupied[i])
            {
                continue;
            }

            if (slots.Any(s => s.SlotIndex == i && (s.IsRerunSlot || s.Candidates.Count > 0)))
            {
                continue;
            }

            Upsert(slots, i, 1, fillQueue[q % fillQueue.Count]);
            q++;
        }
    }

    public static void FillEmptySlotsWithChannelFilter(List<LineupSlotDto> slots, string? filterJson)
    {
        var occupied = new bool[48];
        foreach (var slot in slots.Where(s => s.IsRerunSlot || s.Candidates.Count > 0))
        {
            for (var i = slot.SlotIndex; i < slot.SlotIndex + slot.SpanSlots && i < 48; i++)
            {
                occupied[i] = true;
            }
        }

        var fallback = string.IsNullOrWhiteSpace(filterJson) ? "{}" : filterJson;
        for (var i = 0; i < 48; i++)
        {
            if (occupied[i])
            {
                continue;
            }

            slots.RemoveAll(s => s.SlotIndex == i);
            slots.Add(new LineupSlotDto
            {
                SlotIndex = i,
                SpanSlots = 1,
                Candidates =
                [
                    new SlotCandidateDto
                    {
                        Kind = SlotCandidateKind.FilterQuery,
                        FilterJson = fallback,
                        Weight = 1,
                        SortOrder = 0
                    }
                ]
            });
        }

        slots.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
    }

    private static void TryPlaceMovie(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        DayOfWeek day,
        int startSlot,
        List<AiCatalogEntry> movies,
        int movieIndex)
    {
        if (!weekly.TryGetValue(day, out var slots) || movieIndex >= movies.Count)
        {
            return;
        }

        if (CountMovies(slots) >= 2)
        {
            return;
        }

        var movie = movies[movieIndex % movies.Count];
        var span = Math.Clamp(Math.Max(2, (int)Math.Ceiling(Math.Max(30, movie.RuntimeMinutes) / 30.0)), 2, 8);
        PlaceSeriesOrMovie(slots, movie.Id, startSlot, span, isMovie: true);
    }

    private static int CountMovies(List<LineupSlotDto> slots)
        => slots.Count(s => s.SpanSlots >= 3 && s.Candidates.Count > 0);

    private static void PlaceRerun(List<LineupSlotDto> slots, int start, int length)
    {
        for (var i = 0; i < length && start + i < 48; i++)
        {
            var index = start + i;
            slots.RemoveAll(s => s.SlotIndex == index);
            slots.Add(new LineupSlotDto
            {
                SlotIndex = index,
                SpanSlots = 1,
                IsRerunSlot = true,
                Candidates = []
            });
        }

        slots.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
    }

    private static void PlaceSeriesOrMovie(List<LineupSlotDto> slots, Guid itemId, int start, int length, bool isMovie)
    {
        if (isMovie)
        {
            ClearRange(slots, start, length);
            Upsert(slots, start, length, itemId);
            return;
        }

        for (var i = 0; i < length && start + i < 48; i++)
        {
            Upsert(slots, start + i, 1, itemId);
        }
    }

    private static void ClearRange(List<LineupSlotDto> slots, int start, int length)
    {
        var end = Math.Min(48, start + length);
        slots.RemoveAll(s => s.SlotIndex > start && s.SlotIndex < end);
        foreach (var slot in slots.Where(s => s.SlotIndex < start && s.SlotIndex + s.SpanSlots > start).ToList())
        {
            slot.SpanSlots = Math.Max(1, start - slot.SlotIndex);
        }
    }

    private static void Upsert(List<LineupSlotDto> slots, int start, int span, Guid itemId)
    {
        span = Math.Clamp(span, 1, 48 - start);
        slots.RemoveAll(s => s.SlotIndex >= start && s.SlotIndex < start + span);
        slots.Add(new LineupSlotDto
        {
            SlotIndex = start,
            SpanSlots = span,
            Candidates =
            [
                new SlotCandidateDto
                {
                    Kind = SlotCandidateKind.JellyfinItem,
                    JellyfinItemId = itemId,
                    Weight = 1,
                    SortOrder = 0
                }
            ]
        });
        slots.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
    }

    private static AiCatalogEntry? ResolveEntry(
        AiGeneratedBlock block,
        Dictionary<Guid, AiCatalogEntry> catalogById,
        Dictionary<int, AiCatalogEntry> catalogByN)
    {
        var id = block.JellyfinItemId ?? block.Id ?? block.ItemId;
        if (id is Guid guid && catalogById.TryGetValue(guid, out var byId))
        {
            return byId;
        }

        if (block.N is int n && catalogByN.TryGetValue(n, out var byN))
        {
            return byN;
        }

        if (!string.IsNullOrWhiteSpace(block.Title))
        {
            return catalogById.Values.FirstOrDefault(c =>
                c.Title.Equals(block.Title, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static IReadOnlyList<DayOfWeek> ParseDays(List<string>? days)
    {
        if (days is null || days.Count == 0)
        {
            return Weekdays;
        }

        var parsed = new List<DayOfWeek>();
        foreach (var raw in days)
        {
            var value = raw.Trim().ToLowerInvariant();
            switch (value)
            {
                case "daily" or "all":
                    return Enum.GetValues<DayOfWeek>();
                case "weekdays" or "weekday":
                    return Weekdays;
                case "weekends" or "weekend":
                    return [DayOfWeek.Saturday, DayOfWeek.Sunday];
                case "sun" or "sunday":
                    parsed.Add(DayOfWeek.Sunday);
                    break;
                case "mon" or "monday":
                    parsed.Add(DayOfWeek.Monday);
                    break;
                case "tue" or "tues" or "tuesday":
                    parsed.Add(DayOfWeek.Tuesday);
                    break;
                case "wed" or "wednesday":
                    parsed.Add(DayOfWeek.Wednesday);
                    break;
                case "thu" or "thur" or "thurs" or "thursday":
                    parsed.Add(DayOfWeek.Thursday);
                    break;
                case "fri" or "friday":
                    parsed.Add(DayOfWeek.Friday);
                    break;
                case "sat" or "saturday":
                    parsed.Add(DayOfWeek.Saturday);
                    break;
            }
        }

        return parsed.Count > 0 ? parsed.Distinct().ToList() : Weekdays;
    }

    private static List<LineupSlotDto> CloneSlots(IReadOnlyList<LineupSlotDto> source)
        => source.Select(s => new LineupSlotDto
        {
            SlotIndex = s.SlotIndex,
            SpanSlots = s.SpanSlots,
            IsRerunSlot = s.IsRerunSlot,
            Candidates = (s.Candidates ?? []).Select(c => new SlotCandidateDto
            {
                Kind = c.Kind,
                JellyfinItemId = c.JellyfinItemId,
                CollectionName = c.CollectionName,
                FilterJson = c.FilterJson,
                FinTvListId = c.FinTvListId,
                Weight = c.Weight,
                SortOrder = c.SortOrder
            }).ToList()
        }).ToList();
}
