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
        ChannelContentType contentType,
        AiPlayoutTemplate? template = null,
        string? libraryTag = null)
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
            var runtimeSpan = LineupSlotSpans.SpanFromRuntimeMinutes(Math.Max(0, entry.RuntimeMinutes));
            var length = isMovie
                ? Math.Clamp(Math.Max(runtimeSpan, block.SpanSlots ?? runtimeSpan), 1, 8)
                : Math.Clamp(block.EpisodeBlock ?? block.SpanSlots ?? 2, 1, 6);

            foreach (var day in days)
            {
                var daypartName = AiPlayoutTemplates.GetDaypartNameForSlot(template, start, day);
                if (NetworkClockDaypartMatcher.IsRerunDaypartName(daypartName))
                {
                    continue;
                }

                var place = entry;
                var placeMovie = isMovie;
                var placeLength = length;
                if (NetworkClockDaypartMatcher.IsHardReject(ScoreEntry(place, libraryTag, daypartName)))
                {
                    place = PickBest(
                        catalog,
                        libraryTag,
                        daypartName,
                        [],
                        start,
                        occupied: null,
                        skipMovies: !NetworkClockDaypartMatcher.PrefersMovies(libraryTag, daypartName));
                    if (place is null)
                    {
                        continue;
                    }

                    placeMovie = string.Equals(place.Type, "Movie", StringComparison.OrdinalIgnoreCase);
                    placeLength = placeMovie
                        ? Math.Clamp(LineupSlotSpans.SpanFromRuntimeMinutes(Math.Max(0, place.RuntimeMinutes)), 1, 8)
                        : Math.Min(placeLength, 2);
                }

                var remaining = AiPlayoutTemplates.SlotsRemainingInDaypart(template, start, day);
                var maxEpisodes = NetworkClockDaypartMatcher.MaxSeriesEpisodes(daypartName);
                placeLength = placeMovie
                    ? Math.Min(placeLength, remaining)
                    : Math.Min(placeLength, Math.Min(remaining, maxEpisodes));
                PlaceSeriesOrMovie(weekly[day], place.Id, start, placeLength, placeMovie);
            }
        }

        foreach (var day in weekly.Keys.ToList())
        {
            FillRemainingGaps(weekly[day], catalog, catalogMode, contentType, template, libraryTag, day);
        }

        SprinkleMiniMarathons(weekly, catalog, template, libraryTag);

        return weekly;
    }

    public const int MixedTvWeekendMovieCap = 2;

    /// <summary>
    /// Mixed TV channels keep series as the default. Network-clock days allow movies
    /// only in dayparts that ask for features, capped at two unique titles.
    /// Other templates: weekdays drop leftover movies; Friday-Sunday keep at most two.
    /// </summary>
    public static void LimitMixedTvMovies(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<AiCatalogEntry> catalog,
        ChannelCatalogMode catalogMode,
        ChannelContentType contentType,
        AiPlayoutTemplate? template = null,
        string? libraryTag = null)
    {
        if (catalogMode != ChannelCatalogMode.Mixed || contentType != ChannelContentType.TvShow)
        {
            return;
        }

        var movieIds = MovieIds(catalog);
        if (movieIds.Count == 0)
        {
            return;
        }

        if (ChannelAiRules.IsPrimeTvLibraryTag(libraryTag))
        {
            ApplyPrimeTvMovieCadence(weekly, catalog, movieIds, template, libraryTag);
            return;
        }

        if (AiPlayoutTemplates.UsesNetworkClock(template?.Id))
        {
            LimitNetworkClockMovies(weekly, movieIds, template, libraryTag);
            return;
        }

        foreach (var day in weekly.Keys.ToList())
        {
            var cap = day is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MixedTvWeekendMovieCap
                : 0;
            var slots = weekly[day];
            var extras = slots
                .Where(s => IsMovieSlot(s, movieIds))
                .OrderByDescending(s => s.SlotIndex)
                .Skip(cap)
                .Select(s => s.SlotIndex)
                .ToHashSet();
            if (extras.Count == 0)
            {
                continue;
            }

            slots.RemoveAll(s => extras.Contains(s.SlotIndex));
        }
    }

    public static void SprinkleMovies(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<AiCatalogEntry> catalog,
        ChannelCatalogMode catalogMode,
        string? libraryTag = null)
    {
        if (catalogMode != ChannelCatalogMode.Mixed || ChannelAiRules.IsPrimeTvLibraryTag(libraryTag))
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

        var movieIds = MovieIds(catalog);
        TryPlaceMovie(weekly, DayOfWeek.Friday, 40, movies, movieIds, 0);
        if (movies.Count > 1)
        {
            TryPlaceMovie(weekly, DayOfWeek.Saturday, 36, movies, movieIds, 1);
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

    public static bool IsOvernightRerunSlot(int slotIndex, AiPlayoutTemplate? template)
    {
        var rerun = template?.Dayparts.FirstOrDefault(d =>
            d.Name.Contains("rerun", StringComparison.OrdinalIgnoreCase));
        if (rerun is not null)
        {
            return rerun.ContainsSlot(slotIndex);
        }

        return IsOvernightRerunSlot(slotIndex);
    }

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
        ChannelContentType contentType,
        AiPlayoutTemplate? template = null,
        string? libraryTag = null,
        DayOfWeek? day = null,
        IReadOnlySet<Guid>? excludeIds = null)
    {
        var occupied = new bool[48];
        foreach (var slot in slots.Where(s => s.IsRerunSlot || s.Candidates.Count > 0))
        {
            var span = LineupSlotSpans.ClampSpan(slot.SlotIndex, slot.SpanSlots);
            for (var i = slot.SlotIndex; i < slot.SlotIndex + span && i < 48; i++)
            {
                occupied[i] = true;
            }
        }

            var skipMoviesDefault = catalogMode is ChannelCatalogMode.TvOnly or ChannelCatalogMode.Mixed
            && contentType == ChannelContentType.TvShow;
        var skipMoviesAlways = ChannelAiRules.IsPrimeTvLibraryTag(libraryTag);
        var movieIds = MovieIds(catalog);
        var used = UsedIds(slots);
        for (var i = 0; i < 48; i++)
        {
            if (occupied[i] || slots.Any(s => s.SlotIndex == i && (s.IsRerunSlot || s.Candidates.Count > 0)))
            {
                continue;
            }

            var daypartName = AiPlayoutTemplates.GetDaypartNameForSlot(template, i, day);
            if (NetworkClockDaypartMatcher.IsRerunDaypartName(daypartName))
            {
                continue;
            }

            var moviesToday = CountMovies(slots, movieIds);
            var moviesInDaypart = CountMoviesInDaypart(slots, movieIds, template, day, daypartName);
            var allowMovies = !skipMoviesAlways
                && (!skipMoviesDefault
                    || (NetworkClockDaypartMatcher.PrefersMovies(libraryTag, daypartName)
                        && moviesToday < NetworkClockDaypartMatcher.MaxMoviesPerDay
                        && moviesInDaypart < NetworkClockDaypartMatcher.MaxMoviesForDaypart(libraryTag, daypartName)));
            var chosen = PickBest(catalog, libraryTag, daypartName, used, i, occupied, skipMovies: !allowMovies, excludeIds);
            if (chosen is null && allowMovies)
            {
                chosen = PickBest(catalog, libraryTag, daypartName, used, i, occupied, skipMovies: true, excludeIds);
            }

            if (chosen is null)
            {
                continue;
            }

            var isMovie = string.Equals(chosen.Type, "Movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(chosen.Type, "Clip", StringComparison.OrdinalIgnoreCase);
            var span = SpanThatFits(chosen, i, occupied);
            span = Math.Min(span, AiPlayoutTemplates.SlotsRemainingInDaypart(template, i, day));
            if (!isMovie)
            {
                var perEpisode = LineupSlotSpans.SpanFromRuntimeMinutes(
                    chosen.RuntimeMinutes is > 5 and <= 90 ? chosen.RuntimeMinutes : 30,
                    maxSpan: 2);
                var episodes = 1 + Math.Abs(HashCode.Combine(i, chosen.Id)) % 4;
                episodes = Math.Min(episodes, NetworkClockDaypartMatcher.MaxSeriesEpisodes(daypartName));
                var wanted = Math.Max(1, perEpisode * episodes);
                wanted = Math.Min(wanted, AiPlayoutTemplates.SlotsRemainingInDaypart(template, i, day));
                span = 0;
                while (i + span < 48 && span < wanted && !occupied[i + span])
                {
                    span++;
                }
            }

            if (span <= 0)
            {
                continue;
            }

            Upsert(slots, i, span, chosen.Id);
            used.Add(chosen.Id);
            for (var j = i; j < i + span && j < 48; j++)
            {
                occupied[j] = true;
            }
        }
    }

    public static void ApplyDaypartFit(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<AiCatalogEntry> catalog,
        AiPlayoutTemplate? template,
        string? libraryTag)
    {
        if (template?.Dayparts is not { Count: > 0 })
        {
            return;
        }

        var catalogById = catalog.ToDictionary(c => c.Id);
        var movieIds = MovieIds(catalog);
        foreach (var day in weekly.Keys.ToList())
        {
            FitDay(weekly[day], catalog, catalogById, movieIds, template, libraryTag, day);
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

    /// <summary>
    /// Turns a few weekday/weekend series blocks into 4–6 episode mini-marathons.
    /// </summary>
    public static void SprinkleMiniMarathons(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<AiCatalogEntry> catalog,
        AiPlayoutTemplate? template,
        string? libraryTag)
    {
        _ = libraryTag;
        var seriesIds = catalog
            .Where(entry => string.Equals(entry.Type, "Series", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Id)
            .ToHashSet();
        if (seriesIds.Count == 0)
        {
            return;
        }

        foreach (var day in new[] { DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            if (!weekly.TryGetValue(day, out var slots))
            {
                continue;
            }

            TryExtendToMarathon(slots, seriesIds, template, day);
        }
    }

    private static void TryExtendToMarathon(
        List<LineupSlotDto> slots,
        HashSet<Guid> seriesIds,
        AiPlayoutTemplate? template,
        DayOfWeek day)
    {
        foreach (var slot in slots.OrderBy(s => s.SlotIndex).ToList())
        {
            if (slot.IsRerunSlot)
            {
                continue;
            }

            if (day is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? slot.SlotIndex is < 16 or > 35
                : slot.SlotIndex is < 30 or > 39)
            {
                continue;
            }

            var id = slot.Candidates.FirstOrDefault()?.JellyfinItemId;
            if (id is not Guid seriesId || !seriesIds.Contains(seriesId))
            {
                continue;
            }

            var length = 4 + Math.Abs(HashCode.Combine(slot.SlotIndex, seriesId)) % 3;
            length = Math.Min(length, AiPlayoutTemplates.SlotsRemainingInDaypart(template, slot.SlotIndex, day));
            var max = length;
            for (var i = 1; i < max; i++)
            {
                var index = slot.SlotIndex + i;
                if (index >= 48
                    || slots.Any(s => s.IsRerunSlot && s.SlotIndex <= index && s.SlotIndex + Math.Max(1, s.SpanSlots) > index))
                {
                    max = i;
                    break;
                }
            }

            if (max < 4)
            {
                continue;
            }

            var end = slot.SlotIndex + max;
            slots.RemoveAll(s => s.SlotIndex != slot.SlotIndex && s.SlotIndex >= slot.SlotIndex && s.SlotIndex < end);
            slot.SpanSlots = max;
            slots.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
            return;
        }
    }

    private static void FitDay(
        List<LineupSlotDto> slots,
        IReadOnlyList<AiCatalogEntry> catalog,
        Dictionary<Guid, AiCatalogEntry> catalogById,
        HashSet<Guid> movieIds,
        AiPlayoutTemplate template,
        string? libraryTag,
        DayOfWeek day)
    {
        var usedByDaypart = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots.OrderBy(s => s.SlotIndex).ToList())
        {
            if (slot.IsRerunSlot
                || slots.All(s => s.SlotIndex != slot.SlotIndex)
                || slots.Any(s => s.SlotIndex < slot.SlotIndex && s.SlotIndex + s.SpanSlots > slot.SlotIndex))
            {
                continue;
            }

            var daypartName = AiPlayoutTemplates.GetDaypartNameForSlot(template, slot.SlotIndex, day) ?? string.Empty;
            if (NetworkClockDaypartMatcher.IsRerunDaypartName(daypartName))
            {
                continue;
            }

            if (!usedByDaypart.TryGetValue(daypartName, out var used))
            {
                used = [];
                usedByDaypart[daypartName] = used;
            }

            var remaining = AiPlayoutTemplates.SlotsRemainingInDaypart(template, slot.SlotIndex, day);
            var current = ResolveSlotEntry(slot, catalogById);
            var currentScore = current is null
                ? NetworkClockDaypartMatcher.HardReject
                : ScoreEntry(current, libraryTag, daypartName);
            var currentIsMovie = current is not null && IsMovieEntry(current);
            var moviesToday = CountMovies(slots, movieIds) - (currentIsMovie ? 1 : 0);
            var moviesInDaypart = CountMoviesInDaypart(slots, movieIds, template, day, daypartName)
                - (currentIsMovie ? 1 : 0);
            var skipMovies = ChannelAiRules.IsPrimeTvLibraryTag(libraryTag)
                || NetworkClockDaypartMatcher.MaxMoviesForDaypart(libraryTag, daypartName) <= 0
                || moviesToday >= NetworkClockDaypartMatcher.MaxMoviesPerDay
                || moviesInDaypart >= NetworkClockDaypartMatcher.MaxMoviesForDaypart(libraryTag, daypartName);
            var best = PickBest(catalog, libraryTag, daypartName, used, slot.SlotIndex, occupied: null, skipMovies);
            var bestScore = best is null ? NetworkClockDaypartMatcher.HardReject : ScoreEntry(best, libraryTag, daypartName);
            var replace = current is null
                || NetworkClockDaypartMatcher.IsHardReject(currentScore)
                || (best is not null
                    && best.Id != current.Id
                    && bestScore >= currentScore + 15
                    && currentScore < 10);

            var chosen = replace ? best : current;
            if (chosen is null)
            {
                continue;
            }

            var isMovie = string.Equals(chosen.Type, "Movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(chosen.Type, "Clip", StringComparison.OrdinalIgnoreCase);
            var maxSpan = isMovie
                ? remaining
                : Math.Min(remaining, NetworkClockDaypartMatcher.MaxSeriesEpisodes(daypartName));
            if (replace)
            {
                var span = isMovie
                    ? Math.Min(maxSpan, LineupSlotSpans.SpanFromRuntimeMinutes(Math.Max(30, chosen.RuntimeMinutes)))
                    : maxSpan;
                Upsert(slots, slot.SlotIndex, Math.Clamp(span, 1, maxSpan), chosen.Id);
            }
            else if (slot.SpanSlots > maxSpan)
            {
                slot.SpanSlots = maxSpan;
            }

            used.Add(chosen.Id);
        }
    }

    private static HashSet<Guid> UsedIds(IEnumerable<LineupSlotDto> slots)
        => slots
            .SelectMany(s => s.Candidates)
            .Select(c => c.JellyfinItemId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

    private static AiCatalogEntry? ResolveSlotEntry(LineupSlotDto slot, Dictionary<Guid, AiCatalogEntry> catalogById)
    {
        var id = slot.Candidates.FirstOrDefault()?.JellyfinItemId;
        return id is Guid guid && catalogById.TryGetValue(guid, out var entry) ? entry : null;
    }

    private static int ScoreEntry(AiCatalogEntry entry, string? libraryTag, string? daypartName)
        => NetworkClockDaypartMatcher.Score(
            entry.Title,
            entry.Type,
            entry.Genres,
            entry.OfficialRating,
            entry.Year,
            entry.Plot,
            libraryTag,
            daypartName);

    private static AiCatalogEntry? PickBest(
        IReadOnlyList<AiCatalogEntry> catalog,
        string? libraryTag,
        string? daypartName,
        HashSet<Guid> used,
        int startSlot,
        bool[]? occupied,
        bool skipMovies = false,
        IReadOnlySet<Guid>? excludeIds = null)
    {
        AiCatalogEntry? best = null;
        var bestScore = NetworkClockDaypartMatcher.HardReject;
        var bestUsed = true;
        foreach (var entry in catalog)
        {
            if (excludeIds is { Count: > 0 } && excludeIds.Contains(entry.Id))
            {
                continue;
            }
            if (skipMovies && entry.Type is "Movie" or "Clip")
            {
                continue;
            }

            var score = ScoreEntry(entry, libraryTag, daypartName);
            if (used.Contains(entry.Id))
            {
                score -= IsMovieEntry(entry) ? 80 : 400;
            }

            if (NetworkClockDaypartMatcher.IsHardReject(score))
            {
                continue;
            }

            if (occupied is not null && SpanThatFits(entry, startSlot, occupied) <= 0)
            {
                continue;
            }

            var alreadyUsed = used.Contains(entry.Id);
            if (best is not null
                && (score < bestScore
                    || (score == bestScore && alreadyUsed && !bestUsed)
                    || (score == bestScore && alreadyUsed == bestUsed && (entry.Year ?? int.MaxValue) >= (best.Year ?? int.MaxValue))))
            {
                continue;
            }

            best = entry;
            bestScore = score;
            bestUsed = alreadyUsed;
        }

        return best;
    }

    private static void ApplyPrimeTvMovieCadence(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        IReadOnlyList<AiCatalogEntry> catalog,
        HashSet<Guid> movieIds,
        AiPlayoutTemplate? template,
        string? libraryTag)
    {
        foreach (var day in weekly.Keys.ToList())
        {
            weekly[day].RemoveAll(s => IsMovieSlot(s, movieIds));
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

        var seed = movies.Aggregate(libraryTag?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 17, (hash, movie) => HashCode.Combine(hash, movie.Id));
        var rng = new Random(seed);
        var weekday = Weekdays[rng.Next(Weekdays.Length)];
        PlacePrimeTvMovie(weekly, weekday, movies[0], template);

        var weekendCount = movies.Count == 1 ? 0 : movies.Count == 2 ? 2 : rng.Next(2, 4);
        weekendCount = Math.Min(weekendCount, movies.Count - 1);
        var weekendDays = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday };
        for (var i = 0; i < weekendCount; i++)
        {
            PlacePrimeTvMovie(weekly, weekendDays[i % weekendDays.Length], movies[1 + (i % (movies.Count - 1))], template);
        }
    }

    private static readonly int[] PrimeTvEveningSlots = [36, 38, 40, 42];

    private static void PlacePrimeTvMovie(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        DayOfWeek day,
        AiCatalogEntry movie,
        AiPlayoutTemplate? template)
    {
        if (!weekly.TryGetValue(day, out var slots))
        {
            return;
        }

        var span = Math.Clamp(Math.Max(2, LineupSlotSpans.SpanFromRuntimeMinutes(Math.Max(30, movie.RuntimeMinutes))), 2, 8);
        foreach (var start in PrimeTvEveningSlots)
        {
            var daypart = AiPlayoutTemplates.GetDaypartNameForSlot(template, start, day);
            if (NetworkClockDaypartMatcher.IsRerunDaypartName(daypart))
            {
                continue;
            }

            if (slots.Any(s => s.SlotIndex < start + span && s.SlotIndex + s.SpanSlots > start && s.IsRerunSlot))
            {
                continue;
            }

            PlaceSeriesOrMovie(slots, movie.Id, start, span, isMovie: true);
            return;
        }
    }

    private static void TryPlaceMovie(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        DayOfWeek day,
        int startSlot,
        List<AiCatalogEntry> movies,
        HashSet<Guid> movieIds,
        int movieIndex)
    {
        if (!weekly.TryGetValue(day, out var slots) || movieIndex >= movies.Count)
        {
            return;
        }

        if (CountMovies(slots, movieIds) >= MixedTvWeekendMovieCap)
        {
            return;
        }

        var movie = movies[movieIndex % movies.Count];
        var span = Math.Clamp(Math.Max(2, LineupSlotSpans.SpanFromRuntimeMinutes(Math.Max(30, movie.RuntimeMinutes))), 2, 8);
        PlaceSeriesOrMovie(slots, movie.Id, startSlot, span, isMovie: true);
    }

    private static void LimitNetworkClockMovies(
        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly,
        HashSet<Guid> movieIds,
        AiPlayoutTemplate? template,
        string? libraryTag)
    {
        foreach (var day in weekly.Keys.ToList())
        {
            var slots = weekly[day];
            var extras = new HashSet<int>();
            var keptToday = 0;
            var keptByDaypart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in slots
                .Where(s => IsMovieSlot(s, movieIds))
                .OrderBy(s => MovieKeepRank(template, s.SlotIndex, day, libraryTag))
                .ThenBy(s => s.SlotIndex))
            {
                var daypart = AiPlayoutTemplates.GetDaypartNameForSlot(template, slot.SlotIndex, day) ?? string.Empty;
                var daypartCap = NetworkClockDaypartMatcher.MaxMoviesForDaypart(libraryTag, daypart);
                keptByDaypart.TryGetValue(daypart, out var keptHere);
                if (daypartCap <= 0
                    || keptToday >= NetworkClockDaypartMatcher.MaxMoviesPerDay
                    || keptHere >= daypartCap)
                {
                    extras.Add(slot.SlotIndex);
                    continue;
                }

                keptByDaypart[daypart] = keptHere + 1;
                keptToday++;
            }

            if (extras.Count > 0)
            {
                slots.RemoveAll(s => extras.Contains(s.SlotIndex));
            }
        }
    }

    private static int MovieKeepRank(
        AiPlayoutTemplate? template,
        int slotIndex,
        DayOfWeek day,
        string? libraryTag)
    {
        var daypart = AiPlayoutTemplates.GetDaypartNameForSlot(template, slotIndex, day) ?? string.Empty;
        var key = daypart.ToLowerInvariant();
        if (key.Contains("prime"))
        {
            return 0;
        }

        if (key.Contains("late"))
        {
            return 1;
        }

        return NetworkClockDaypartMatcher.PrefersMovies(libraryTag, daypart) ? 2 : 9;
    }

    private static int CountMoviesInDaypart(
        List<LineupSlotDto> slots,
        HashSet<Guid> movieIds,
        AiPlayoutTemplate? template,
        DayOfWeek? day,
        string? daypartName)
    {
        if (string.IsNullOrWhiteSpace(daypartName))
        {
            return 0;
        }

        return slots.Count(slot =>
            IsMovieSlot(slot, movieIds)
            && string.Equals(
                AiPlayoutTemplates.GetDaypartNameForSlot(template, slot.SlotIndex, day),
                daypartName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static int CountMovies(List<LineupSlotDto> slots, HashSet<Guid> movieIds)
        => slots.Count(s => IsMovieSlot(s, movieIds));

    private static HashSet<Guid> MovieIds(IEnumerable<AiCatalogEntry> catalog)
        => catalog
            .Where(c => string.Equals(c.Type, "Movie", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .ToHashSet();

    private static bool IsMovieEntry(AiCatalogEntry entry)
        => string.Equals(entry.Type, "Movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, "Clip", StringComparison.OrdinalIgnoreCase);

    private static bool IsMovieSlot(LineupSlotDto slot, HashSet<Guid> movieIds)
        => !slot.IsRerunSlot
            && slot.Candidates.Any(c => c.JellyfinItemId is Guid id && movieIds.Contains(id));

    private static int SpanThatFits(AiCatalogEntry entry, int start, bool[] occupied)
    {
        var isMovie = string.Equals(entry.Type, "Movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, "Clip", StringComparison.OrdinalIgnoreCase);
        var span = string.Equals(entry.Type, "Series", StringComparison.OrdinalIgnoreCase)
            ? LineupSlotSpans.SpanFromRuntimeMinutes(
                entry.RuntimeMinutes is > 5 and <= 90 ? entry.RuntimeMinutes : 30,
                maxSpan: 2)
            : LineupSlotSpans.SpanFromRuntimeMinutes(entry.RuntimeMinutes);
        span = LineupSlotSpans.ClampSpan(start, span);

        var free = 0;
        while (start + free < 48 && !occupied[start + free])
        {
            free++;
        }

        if (free <= 0)
        {
            return 0;
        }

        if (isMovie && span > free && free < 2)
        {
            return 0;
        }

        return Math.Min(span, free);
    }

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
