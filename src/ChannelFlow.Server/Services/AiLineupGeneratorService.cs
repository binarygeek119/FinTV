using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class AiLineupGeneratorService
{
    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;

    private readonly FinTvDbContext _db;
    private readonly AiCatalogManifestBuilder _manifestBuilder;
    private readonly LlmClientService _llm;
    private readonly JellyfinCatalogService _catalog;
    private readonly LineupService _lineups;
    private readonly HolidayChannelService _holidays;
    private readonly ILogger<AiLineupGeneratorService> _logger;

    public AiLineupGeneratorService(
        FinTvDbContext db,
        AiCatalogManifestBuilder manifestBuilder,
        LlmClientService llm,
        JellyfinCatalogService catalog,
        LineupService lineups,
        HolidayChannelService holidays,
        ILogger<AiLineupGeneratorService> logger)
    {
        _db = db;
        _manifestBuilder = manifestBuilder;
        _llm = llm;
        _catalog = catalog;
        _lineups = lineups;
        _holidays = holidays;
        _logger = logger;
    }

    public async Task<AiLineupPreviewResult> GenerateAsync(
        Guid channelId,
        AiProvider? providerOverride,
        CancellationToken cancellationToken = default)
    {
        EnsureAiEnabled();

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        if (channel.ContentType is ChannelContentType.Weather or ChannelContentType.News)
        {
            throw new InvalidOperationException("Weather channels do not use AI lineups.");
        }

        if (_holidays.IsHolidayChannel(channel))
        {
            var scheduleDate = _holidays.GetScheduleDateUtc(DateTime.UtcNow);
            if (_holidays.GetActiveHoliday(scheduleDate) is null)
            {
                throw new InvalidOperationException(
                    "The Holiday Channel is off-season. AI lineups are generated when a holiday window becomes active (up to 30 days before).");
            }
        }

        var manifest = _manifestBuilder.Build(channel);
        FinTvDebugLog.Ai(
            _logger,
            "Catalog manifest for {Channel}: mode={Mode}, tagMatched={TagMatched}, available={Available}, inPrompt={InPrompt}",
            channel.Name,
            manifest.CatalogMode,
            manifest.TagMatchedCount,
            manifest.TotalAvailable,
            manifest.IncludedInPrompt);

        if (manifest.Catalog.Count == 0)
        {
            if (_holidays.IsHolidayChannel(channel))
            {
                throw new InvalidOperationException(
                    "No holiday-themed shows or movies found — tag items with holiday keywords in Jellyfin tags, genres, or plot.");
            }

            throw new InvalidOperationException(BuildEmptyCatalogError(channel, manifest));
        }

        var libraryTag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        var ruleBrief = ChannelAiRules.GetBrief(libraryTag);
        var catalogMode = manifest.CatalogMode;
        var playoutTemplate = AiPlayoutTemplates.Resolve(channel);
        var provider = providerOverride ?? FinTvRuntime.Current?.Configuration.Ai.DefaultProvider ?? AiProvider.OpenAi;

        var systemPrompt = BuildSystemPrompt(catalogMode, channel.ContentType, playoutTemplate);
        var userPrompt = BuildUserPrompt(channel, manifest, ruleBrief, catalogMode, playoutTemplate);
        FinTvDebugLog.Ai(
            _logger,
            "LLM request for {Channel} via {Provider}: systemPrompt={SystemChars} chars, userPrompt={UserChars} chars, template={Template}",
            channel.Name,
            provider,
            systemPrompt.Length,
            userPrompt.Length,
            playoutTemplate.Id);

        Dictionary<DayOfWeek, List<LineupSlotDto>> weekly;
        List<LineupSlotDto> slots;

        if (channel.ContentType == ChannelContentType.MusicVideo)
        {
            slots = NetworkSchedulePlanner.CreateFilterSlots(channel.FilterJson);
            weekly = NetworkSchedulePlanner.CloneDailyToWeek(slots);
        }
        else
        {
            var rawJson = await _llm.CompleteJsonAsync(provider, systemPrompt, userPrompt, cancellationToken);
            var aiResponse = ParseAiResponse(rawJson);
            FinTvDebugLog.Ai(
                _logger,
                "LLM response for {Channel}: {ResponseChars} chars, slotsReturned={Slots}, blocksReturned={Blocks}",
                channel.Name,
                rawJson.Length,
                aiResponse.Slots?.Count ?? 0,
                aiResponse.Blocks?.Count ?? 0);

            var validIds = manifest.Catalog.Select(c => c.Id).ToHashSet();
            var catalogById = manifest.Catalog.ToDictionary(c => c.Id);
            var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
            slots = ValidateAndBuildSlots(
                aiResponse.Slots,
                validIds,
                catalogById,
                manifest.Catalog,
                channel.FilterJson,
                yearConstraints,
                playoutTemplate,
                catalogMode);

            weekly = aiResponse.Blocks is { Count: > 0 }
                ? NetworkSchedulePlanner.ExpandBlocks(aiResponse.Blocks, manifest.Catalog, catalogMode, channel.ContentType)
                : NetworkSchedulePlanner.CloneDailyToWeek(slots);

            NetworkSchedulePlanner.SprinkleMovies(weekly, manifest.Catalog, catalogMode);
            if (channel.ContentType == ChannelContentType.TvShow)
            {
                NetworkSchedulePlanner.ClearOvernightSlots(weekly);
            }

            slots = weekly.GetValueOrDefault(DayOfWeek.Monday) ?? slots;
        }

        var filledBlocks = slots.Count(s => s.Candidates.Count > 0);
        var coveredHalfHours = slots.Where(s => s.Candidates.Count > 0).Sum(s => Math.Clamp(s.SpanSlots, 1, 48));
        FinTvDebugLog.Ai(
            _logger,
            "Validated lineup for {Channel}: {FilledBlocks} blocks covering {Covered}/48 half-hours, weeklyDays={Days}",
            channel.Name,
            filledBlocks,
            Math.Min(48, coveredHalfHours),
            weekly.Count);

        return BuildPreview(channel, slots, manifest, provider, playoutTemplate, weekly);
    }

    public async Task ApplyAsync(
        Guid channelId,
        IReadOnlyList<LineupSlotDto> slots,
        bool rebuildPlayout,
        LineupGeneratorService generator,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<DayOfWeek, List<LineupSlotDto>>? weeklyLineups = null)
    {
        EnsureAiEnabled();
        FinTvDebugLog.Ai(
            _logger,
            "Applying AI lineup to {ChannelId}: {SlotCount} slots, rebuildPlayout={Rebuild}, weeklyDays={Days}",
            channelId,
            slots.Count,
            rebuildPlayout,
            weeklyLineups?.Count ?? 0);

        var weekly = weeklyLineups is { Count: > 0 }
            ? weeklyLineups.ToDictionary(kv => kv.Key, kv => NormalizeSlots(kv.Value))
            : NetworkSchedulePlanner.CloneDailyToWeek(NormalizeSlots(slots));
        var defaultSlots = weekly.GetValueOrDefault(DayOfWeek.Monday) ?? NormalizeSlots(slots);

        await _lineups.UpdateDefaultSlotsAsync(channelId, defaultSlots, cancellationToken);
        await _lineups.ReplaceWeeklyDayLineupsAsync(channelId, weekly, cancellationToken);
        _db.ChangeTracker.Clear();

        if (!rebuildPlayout)
        {
            return;
        }

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        try
        {
            var start = DateTime.UtcNow.Date;
            var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
            FinTvDebugLog.Ai(
                _logger,
                "Rebuilding playout for {Channel} from {Start:u} to {End:u}",
                channel.Name,
                start,
                end);
            await generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
            var itemCount = await _db.PlayoutItems.CountAsync(
                p => p.ChannelId == channelId && p.Finish > DateTime.UtcNow,
                cancellationToken);
            FinTvDebugLog.Ai(
                _logger,
                "Playout rebuild finished for {Channel}: {ItemCount} future items",
                channel.Name,
                itemCount);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Invalid schedule time zone in ChannelFlow settings. Set Dashboard → Plugins → ChannelFlow → schedule time zone to a valid IANA id (e.g. America/New_York).",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Playout rebuild failed: {ex.Message}", ex);
        }
    }

    private AiLineupPreviewResult BuildPreview(
        Channel channel,
        List<LineupSlotDto> slots,
        AiCatalogManifest manifest,
        AiProvider provider,
        AiPlayoutTemplate playoutTemplate,
        Dictionary<DayOfWeek, List<LineupSlotDto>>? weeklyLineups = null)
    {
        var previewSlots = slots
            .OrderBy(s => s.SlotIndex)
            .Select(slot =>
            {
                var candidate = slot.Candidates.FirstOrDefault();
                AiCatalogEntry? entry = null;
                if (candidate?.JellyfinItemId is Guid id && manifest.Catalog.FirstOrDefault(c => c.Id == id) is { } found)
                {
                    entry = found;
                }

                return new AiLineupPreviewSlot
                {
                    SlotIndex = slot.SlotIndex,
                    SpanSlots = slot.SpanSlots,
                    DaypartName = AiPlayoutTemplates.GetDaypartNameForSlot(playoutTemplate, slot.SlotIndex),
                    Title = entry?.Title ?? candidate?.CollectionName ?? "Filter fallback",
                    Type = entry?.Type ?? candidate?.Kind.ToString() ?? string.Empty,
                    RuntimeMinutes = entry?.RuntimeMinutes,
                    JellyfinItemId = candidate?.JellyfinItemId
                };
            })
            .ToList();

        return new AiLineupPreviewResult
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Provider = provider,
            CatalogMode = manifest.CatalogMode,
            PlayoutTemplateId = playoutTemplate.Id,
            PlayoutTemplateName = playoutTemplate.Name,
            CatalogSummary = new AiCatalogSummary
            {
                TotalAvailable = manifest.TotalAvailable,
                IncludedInPrompt = manifest.IncludedInPrompt
            },
            Slots = previewSlots,
            LineupSlots = slots,
            WeeklyLineups = weeklyLineups
        };
    }

    private static string BuildEmptyCatalogError(Channel channel, AiCatalogManifest manifest)
    {
        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        var genreConstraints = ChannelAiRules.GetGenreConstraints(channel);
        var libraryConstraints = ChannelAiRules.GetLibraryConstraints(channel);
        var channelFilter = FilterDefinition.Parse(channel.FilterJson);

        if (manifest.TagMatchedCount > 0 && yearConstraints is not null)
        {
            return
                $"Found {manifest.TagMatchedCount} library item(s) but none match release years {yearConstraints.MinYear}–{yearConstraints.MaxYear}. "
                + "Add Premiere Date or Production Year metadata in Jellyfin (series use first-episode year).";
        }

        if (manifest.TagMatchedCount > 0 && genreConstraints is not null)
        {
            var genreHint = genreConstraints.RequiredGenreKeywords.Count > 0
                ? string.Join(", ", genreConstraints.RequiredGenreKeywords)
                : "this channel's genre rules";
            var plotHint = genreConstraints.RequiredPlotKeywords.Count > 0
                ? " or plot/overview keywords"
                : string.Empty;
            return
                $"Found {manifest.TagMatchedCount} library item(s) but none match the required genres ({genreHint}){plotHint}. "
                + "Check genre and plot metadata on your shows and movies in Jellyfin.";
        }

        if (manifest.TagMatchedCount > 0 && !string.IsNullOrWhiteSpace(channelFilter?.MaxRating))
        {
            return
                $"Found {manifest.TagMatchedCount} library item(s) but none are rated {channelFilter.MaxRating} or lower. "
                + "Set Official Rating metadata in Jellyfin or adjust the channel filter.";
        }

        if (manifest.TagMatchedCount > 0 && libraryConstraints is not null)
        {
            return
                $"No items found in the Jellyfin library \"{libraryConstraints.LibraryName}\" for this channel.";
        }

        if (manifest.TagMatchedCount > 0)
        {
            return
                $"Found {manifest.TagMatchedCount} library item(s) but none match this channel's catalog mode ({manifest.CatalogMode}).";
        }

        if (libraryConstraints is not null)
        {
            return $"No content found in the Jellyfin library \"{libraryConstraints.LibraryName}\".";
        }

        if (yearConstraints is not null)
        {
            return
                $"No TV shows or movies were found in the ChannelFlow catalog for {channel.Name} "
                + $"({yearConstraints.MinYear}–{yearConstraints.MaxYear}). "
                + "Sync the catalog, confirm TV and movie libraries are selected on the Catalog tab, "
                + "and make sure series have a premiere or first-episode year.";
        }

        return "No matching content found in your Jellyfin library for this channel. Ensure items have genres, release years, and ratings metadata.";
    }

    private static List<LineupSlotDto> ValidateAndBuildSlots(
        List<AiGeneratedSlot>? aiSlots,
        HashSet<Guid> validIds,
        Dictionary<Guid, AiCatalogEntry> catalogById,
        IReadOnlyList<AiCatalogEntry> catalogInPromptOrder,
        string? channelFilterJson,
        ChannelCatalogYearConstraints? yearConstraints,
        AiPlayoutTemplate? playoutTemplate = null,
        ChannelCatalogMode catalogMode = ChannelCatalogMode.TvOnly)
    {
        var occupied = new bool[48];
        var result = ChannelService.CreateEmptySlots()
            .Select(s => new LineupSlotDto { SlotIndex = s.SlotIndex, SpanSlots = 1 })
            .ToDictionary(s => s.SlotIndex);

        var aiPickOrder = new List<Guid>();
        foreach (var aiSlot in aiSlots ?? new List<AiGeneratedSlot>())
        {
            if (aiSlot.SlotIndex < 0 || aiSlot.SlotIndex >= 48)
            {
                continue;
            }

            var span = Math.Clamp(aiSlot.SpanSlots ?? 1, 1, 8);
            if (aiSlot.SlotIndex + span > 48)
            {
                span = 48 - aiSlot.SlotIndex;
            }

            if (IsRangeOccupied(occupied, aiSlot.SlotIndex, span))
            {
                continue;
            }

            var candidateId = ResolveCatalogId(aiSlot, validIds, catalogInPromptOrder);
            if (!candidateId.HasValue)
            {
                continue;
            }

            if (catalogById.TryGetValue(candidateId.Value, out var entry))
            {
                if (yearConstraints is not null
                    && entry.Year.HasValue
                    && !yearConstraints.ContainsYear(entry.Year))
                {
                    continue;
                }

                span = ComputePlayoutSpan(entry, aiSlot.SpanSlots, GetMaxSpanSlots(playoutTemplate));
                if (aiSlot.SlotIndex + span > 48)
                {
                    span = 48 - aiSlot.SlotIndex;
                }

                if (IsRangeOccupied(occupied, aiSlot.SlotIndex, span))
                {
                    continue;
                }
            }

            if (!aiPickOrder.Contains(candidateId.Value))
            {
                aiPickOrder.Add(candidateId.Value);
            }

            MarkOccupied(occupied, aiSlot.SlotIndex, span);
            result[aiSlot.SlotIndex] = new LineupSlotDto
            {
                SlotIndex = aiSlot.SlotIndex,
                SpanSlots = span,
                Candidates =
                [
                    new SlotCandidateDto
                    {
                        Kind = SlotCandidateKind.JellyfinItem,
                        JellyfinItemId = candidateId.Value,
                        Weight = 1,
                        SortOrder = 0
                    }
                ]
            };

            for (var covered = aiSlot.SlotIndex + 1; covered < aiSlot.SlotIndex + span && covered < 48; covered++)
            {
                result.Remove(covered);
            }
        }

        if (ShouldPackMarathon(playoutTemplate, catalogMode))
        {
            return PackMarathonSlots(catalogById, channelFilterJson, yearConstraints, playoutTemplate);
        }

        FillEmptySlotsFromCatalog(
            result,
            occupied,
            catalogById,
            preferSeries: catalogMode is ChannelCatalogMode.TvOnly or ChannelCatalogMode.Mixed);
        return FillEmptySlotsWithFilterFallback(result, occupied, channelFilterJson);
    }

    private static bool ShouldPackMarathon(AiPlayoutTemplate? template, ChannelCatalogMode catalogMode)
        => false;

    private static int GetMaxSpanSlots(AiPlayoutTemplate? template)
    {
        if (template?.Dayparts is not { Count: > 0 } dayparts)
        {
            return 8;
        }

        return dayparts.Max(d => d.MaxSpanSlots ?? 8);
    }

    private static List<LineupSlotDto> PackMarathonSlots(
        Dictionary<Guid, AiCatalogEntry> catalogById,
        string? channelFilterJson,
        ChannelCatalogYearConstraints? yearConstraints,
        AiPlayoutTemplate? playoutTemplate)
    {
        var maxSpan = GetMaxSpanSlots(playoutTemplate);
        var moviesFirst = playoutTemplate?.Id is "movie-marathon" or "holiday-channel";
        var fillQueue = BuildAiredOrderFillQueue(catalogById, yearConstraints, moviesFirst);
        var occupied = new bool[48];
        var result = new Dictionary<int, LineupSlotDto>();
        var cursor = 0;

        foreach (var itemId in fillQueue)
        {
            if (cursor >= 48)
            {
                break;
            }

            if (!catalogById.TryGetValue(itemId, out var entry))
            {
                continue;
            }

            if (yearConstraints is not null
                && entry.Year.HasValue
                && !yearConstraints.ContainsYear(entry.Year))
            {
                continue;
            }

            var span = ComputeSpanFromRuntime(entry.RuntimeMinutes, maxSpan);
            if (cursor + span > 48)
            {
                span = 48 - cursor;
            }

            if (span <= 0)
            {
                break;
            }

            MarkOccupied(occupied, cursor, span);
            result[cursor] = new LineupSlotDto
            {
                SlotIndex = cursor,
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
            };
            cursor += span;
        }

        FillEmptySlotsFromCatalog(result, occupied, catalogById, preferSeries: true);
        return FillEmptySlotsWithFilterFallback(result, occupied, channelFilterJson);
    }

    private static List<Guid> BuildAiredOrderFillQueue(
        Dictionary<Guid, AiCatalogEntry> catalogById,
        ChannelCatalogYearConstraints? yearConstraints,
        bool moviesFirst = false)
        => OrderChronologically(
                catalogById.Values.Where(e => yearConstraints is null
                    || (!e.Year.HasValue || yearConstraints.ContainsYear(e.Year))),
                moviesFirst)
            .Select(e => e.Id)
            .ToList();

    private static IEnumerable<AiCatalogEntry> OrderChronologically(
        IEnumerable<AiCatalogEntry> entries,
        bool moviesFirst)
    {
        if (moviesFirst)
        {
            return entries
                .OrderBy(e => e.Type == "Movie" ? 0 : 1)
                .ThenBy(e => e.Year ?? int.MaxValue)
                .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
        }

        return entries
            .OrderBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
    }

    private static List<LineupSlotDto> FillEmptySlotsFromCatalog(
        Dictionary<int, LineupSlotDto> result,
        bool[] occupied,
        Dictionary<Guid, AiCatalogEntry> catalogById,
        bool preferSeries = false)
    {
        if (catalogById.Count == 0)
        {
            return result.Values.OrderBy(s => s.SlotIndex).ToList();
        }

        var fillQueue = catalogById.Values
            .OrderBy(e => preferSeries && e.Type is "Movie" or "Clip" ? 1 : 0)
            .ThenBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Id)
            .ToList();

        var queueIndex = 0;
        for (var slotIndex = 0; slotIndex < 48; slotIndex++)
        {
            if (occupied[slotIndex])
            {
                continue;
            }

            var itemId = fillQueue[queueIndex % fillQueue.Count];
            queueIndex++;
            occupied[slotIndex] = true;
            result[slotIndex] = new LineupSlotDto
            {
                SlotIndex = slotIndex,
                SpanSlots = 1,
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
            };
        }

        return result.Values.OrderBy(s => s.SlotIndex).ToList();
    }

    private static List<LineupSlotDto> FillEmptySlotsWithFilterFallback(
        Dictionary<int, LineupSlotDto> result,
        bool[] occupied,
        string? channelFilterJson)
    {
        var fallbackFilter = string.IsNullOrWhiteSpace(channelFilterJson)
            ? "{}"
            : channelFilterJson;

        for (var i = 0; i < 48; i++)
        {
            if (occupied[i])
            {
                continue;
            }

            result[i] = new LineupSlotDto
            {
                SlotIndex = i,
                SpanSlots = 1,
                Candidates =
                [
                    new SlotCandidateDto
                    {
                        Kind = SlotCandidateKind.FilterQuery,
                        FilterJson = fallbackFilter,
                        Weight = 1,
                        SortOrder = 0
                    }
                ]
            };
            occupied[i] = true;
        }

        return result.Values.OrderBy(s => s.SlotIndex).ToList();
    }

    private static int ComputeSpanFromRuntime(int runtimeMinutes, int maxSpan = 8)
        => Math.Clamp((int)Math.Ceiling(runtimeMinutes / 30.0), 1, maxSpan);

    /// <summary>
    /// Series IDs play the next episode per slot, so span is one episode (30 or 60 min),
    /// not the full-series runtime Jellyfin sometimes stores on the series record.
    /// </summary>
    private static int ComputePlayoutSpan(AiCatalogEntry entry, int? requestedSpan, int maxSpan)
    {
        if (string.Equals(entry.Type, "Series", StringComparison.OrdinalIgnoreCase))
        {
            var episodeMinutes = entry.RuntimeMinutes is > 5 and <= 90 ? entry.RuntimeMinutes : 30;
            return ComputeSpanFromRuntime(episodeMinutes, Math.Min(2, maxSpan));
        }

        if (entry.RuntimeMinutes > 0)
        {
            return ComputeSpanFromRuntime(entry.RuntimeMinutes, maxSpan);
        }

        return Math.Clamp(requestedSpan ?? 1, 1, maxSpan);
    }

    private static Guid? ResolveCatalogId(
        AiGeneratedSlot slot,
        HashSet<Guid> validIds,
        IReadOnlyList<AiCatalogEntry> catalogInPromptOrder)
    {
        var id = slot.JellyfinItemId
            ?? slot.Id
            ?? slot.ItemId
            ?? slot.Candidates?.FirstOrDefault()?.JellyfinItemId
            ?? slot.Candidates?.FirstOrDefault()?.Id;
        if (id is Guid guid && validIds.Contains(guid))
        {
            return guid;
        }

        if (slot.N is int n && n >= 1 && n <= catalogInPromptOrder.Count)
        {
            return catalogInPromptOrder[n - 1].Id;
        }

        var title = slot.Title ?? slot.Name;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var needle = NormalizeTitle(title);
        if (needle.Length < 3)
        {
            return null;
        }

        AiCatalogEntry? fallback = null;
        foreach (var entry in catalogInPromptOrder)
        {
            var haystack = NormalizeTitle(entry.Title);
            if (haystack.Length == 0)
            {
                continue;
            }

            if (haystack != needle && !haystack.Contains(needle, StringComparison.Ordinal) && !needle.Contains(haystack, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(slot.Type)
                && !string.Equals(entry.Type, slot.Type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (slot.Year is int year && entry.Year is int entryYear && year != entryYear)
            {
                continue;
            }

            if (haystack == needle)
            {
                return entry.Id;
            }

            fallback ??= entry;
        }

        return fallback?.Id;
    }

    private static string NormalizeTitle(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        return Regex.Replace(trimmed, @"[^a-z0-9]+", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    }

    private static bool IsRangeOccupied(bool[] occupied, int start, int span)
    {
        for (var i = start; i < start + span && i < occupied.Length; i++)
        {
            if (occupied[i])
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkOccupied(bool[] occupied, int start, int span)
    {
        for (var i = start; i < start + span && i < occupied.Length; i++)
        {
            occupied[i] = true;
        }
    }

    private static List<LineupSlotDto> NormalizeSlots(IReadOnlyList<LineupSlotDto> slots)
    {
        var normalized = ChannelService.CreateEmptySlots()
            .Select(s => new LineupSlotDto { SlotIndex = s.SlotIndex, SpanSlots = 1 })
            .ToDictionary(s => s.SlotIndex);

        foreach (var slot in slots)
        {
            if (slot.SlotIndex is < 0 or >= 48)
            {
                continue;
            }

            normalized[slot.SlotIndex] = new LineupSlotDto
            {
                SlotIndex = slot.SlotIndex,
                SpanSlots = Math.Clamp(slot.SpanSlots, 1, 8),
                Candidates = slot.Candidates ?? new List<SlotCandidateDto>()
            };
        }

        return normalized.Values.OrderBy(s => s.SlotIndex).ToList();
    }

    private static AiLineupAiResponse ParseAiResponse(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AiLineupAiResponse>(rawJson, JsonOptions)
                ?? new AiLineupAiResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("AI returned invalid JSON for lineup generation.", ex);
        }
    }

    private static string BuildSystemPrompt(
        ChannelCatalogMode catalogMode,
        ChannelContentType contentType,
        AiPlayoutTemplate playoutTemplate)
    {
        var templateSection = AiPlayoutTemplates.BuildPromptSection(playoutTemplate);
        var templateBlock = string.IsNullOrWhiteSpace(templateSection)
            ? string.Empty
            : "\n" + templateSection;

        var mixedRule = catalogMode == ChannelCatalogMode.Mixed
            ? contentType == ChannelContentType.Movie
                ? "\n- Mixed movie channel: movies are the default. TV series are optional holiday/thematic filler only."
                : "\n- Mixed TV channel: series are the default. After the weekly grid is set, ChannelFlow may add at most 1-2 movies on Friday night and/or weekend. Do not load weekdays with movies."
            : string.Empty;

        var formatHint = contentType == ChannelContentType.Movie
            ? """
            Prefer `blocks` for a weekly movie grid (double features, Friday/Saturday nights sticky).
            Movies use spanSlots from runtime. Keep the same titles in the same clock times each week.
            """
            : """
            Prefer `blocks` for a weekly TV grid. ChannelFlow expands this to 14 days and plays episodes in order (S01E01 onward, continuing from the last generated episode).
            - episodeBlock: consecutive 30-minute episodes of the same series (usually 2-4; a theme day may use 2-6).
            - days: weekdays, daily, weekends, or a list such as ["mon","tue","wed","thu","fri"] or ["fri"].
            - If Show X is on at 11:00 every Monday, list only Monday for that block. If Show Y is on at noon every day, use daily.
            - Leave overnight rerun slots 4-11 (2:00-6:00am) empty; ChannelFlow fills those with the previous day's primetime reruns.
            """ + (playoutTemplate.Id is "classic-cable" or "kids-all-day" or "slappy-comedy"
                ? """

            - Morning: a small cartoon block that matches the channel, then regular daytime series.
            - 2:30-4:00pm (slots 29-31): after-school cartoons. Then teen hour. Then primetime. 9:00pm late-night adult. 12:00am adult cartoons.
            """
                : """

            - Follow the playout template dayparts for this channel. Keep the same series in the same clock times each weekday or each week.
            """);

        return """
            You are a cable-network scheduler for ChannelFlow.
            Build a STICKY weekly programming grid, not a one-off random day.
            Reply with JSON only.
            Preferred shape:
            {"blocks":[{"n":1,"title":"Show Name","startSlot":18,"episodeBlock":2,"days":["mon","tue","wed","thu","fri"],"kind":"series"}]}
            Fallback daily shape (cloned to every weekday if blocks are missing):
            {"slots":[{"slotIndex":0,"spanSlots":1,"n":1,"jellyfinItemId":"guid","title":"Show Name"}]}
            Rules:
            - Identify catalog rows with n (preferred), jellyfinItemId, or exact title. Do not invent GUIDs.
            - Keep shows in the same time slot across days/weeks unless it is a weekly special (Monday-only, Friday movie, theme day).
            - Typical series blocks are 2-4 episodes. Include at most one theme-day mini-marathon of 2-6 episodes per week.
            - Schedule like a real TV network using the playout template dayparts.
            """ + mixedRule + "\n" + formatHint + $"\nCatalog mode: {catalogMode}." + templateBlock;
    }

    private string BuildUserPrompt(
        Channel channel,
        AiCatalogManifest manifest,
        string ruleBrief,
        ChannelCatalogMode catalogMode,
        AiPlayoutTemplate playoutTemplate)
    {
        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        var genreConstraints = ChannelAiRules.GetGenreConstraints(channel);
        var libraryConstraints = ChannelAiRules.GetLibraryConstraints(channel);
        var channelFilter = FilterDefinition.Parse(channel.FilterJson);
        HolidayDefinition? activeHoliday = null;
        if (_holidays.IsHolidayChannel(channel))
        {
            activeHoliday = _holidays.GetActiveHoliday(_holidays.GetScheduleDateUtc(DateTime.UtcNow));
        }

        var payload = new
        {
            channel = new
            {
                name = channel.Name,
                number = channel.Number,
                catalogMode = catalogMode.ToString(),
                contentType = channel.ContentType.ToString()
            },
            rules = ruleBrief,
            activeHoliday = activeHoliday is null
                ? null
                : new
                {
                    id = activeHoliday.Id,
                    name = activeHoliday.Name,
                    matchKeywords = activeHoliday.MatchKeywords
                },
            releaseYearFilter = yearConstraints is null
                ? null
                : new
                {
                    minYear = yearConstraints.MinYear,
                    maxYear = yearConstraints.MaxYear,
                    seriesUsesFirstEpisodeYear = yearConstraints.UseFirstEpisodeYearForSeries
                },
            genreFilter = genreConstraints is null
                ? null
                : new
                {
                    requiredKeywords = genreConstraints.RequiredGenreKeywords,
                    excludedKeywords = genreConstraints.ExcludedGenreKeywords
                },
            ratingFilter = string.IsNullOrWhiteSpace(channelFilter?.MinRating)
                && string.IsNullOrWhiteSpace(channelFilter?.MaxRating)
                ? null
                : new
                {
                    minRating = channelFilter?.MinRating,
                    maxRating = channelFilter?.MaxRating
                },
            libraryFilter = libraryConstraints is null
                ? null
                : new
                {
                    libraryName = libraryConstraints.LibraryName
                },
            playoutTemplate = playoutTemplate.Dayparts.Count == 0
                ? null
                : new
                {
                    id = playoutTemplate.Id,
                    name = playoutTemplate.Name,
                    dayparts = playoutTemplate.Dayparts.Select(d => new
                    {
                        name = d.Name,
                        slotRange = d.FormatSlotRange(),
                        brief = d.Brief,
                        maxSpanSlots = d.MaxSpanSlots
                    })
                },
            fineTune = channel.AiFineTunePrompt ?? string.Empty,
            catalog = manifest.Catalog.Select((c, index) => new
            {
                n = index + 1,
                jellyfinItemId = c.Id,
                title = c.Title,
                type = c.Type,
                year = c.Year,
                runtimeMinutes = c.RuntimeMinutes,
                genres = c.Genres
            }),
            totalAvailable = manifest.TotalAvailable
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static void EnsureAiEnabled()
    {
        if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
        {
            throw new InvalidOperationException("AI lineup generation is disabled.");
        }
    }
}

public class AiLineupPreviewResult
{
    public Guid ChannelId { get; set; }

    public string ChannelName { get; set; } = string.Empty;

    public AiProvider Provider { get; set; }

    public ChannelCatalogMode CatalogMode { get; set; }

    public string? PlayoutTemplateId { get; set; }

    public string? PlayoutTemplateName { get; set; }

    public AiCatalogSummary CatalogSummary { get; set; } = new();

    public List<AiLineupPreviewSlot> Slots { get; set; } = new();

    public List<LineupSlotDto> LineupSlots { get; set; } = new();

    public Dictionary<DayOfWeek, List<LineupSlotDto>>? WeeklyLineups { get; set; }
}

public class AiLineupPreviewSlot
{
    public int SlotIndex { get; set; }

    public int SpanSlots { get; set; } = 1;

    public string? DaypartName { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int? RuntimeMinutes { get; set; }

    public Guid? JellyfinItemId { get; set; }
}

public class AiCatalogSummary
{
    public int TotalAvailable { get; set; }

    public int IncludedInPrompt { get; set; }
}

internal class AiLineupAiResponse
{
    public List<AiGeneratedSlot>? Slots { get; set; }

    public List<AiGeneratedBlock>? Blocks { get; set; }
}

internal class AiGeneratedBlock
{
    [JsonPropertyName("n")]
    public int? N { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public Guid? Id { get; set; }

    public Guid? ItemId { get; set; }

    public string? Title { get; set; }

    public int StartSlot { get; set; }

    public int? EpisodeBlock { get; set; }

    public int? SpanSlots { get; set; }

    public List<string>? Days { get; set; }

    public string? Kind { get; set; }

    public bool ThemeDay { get; set; }
}

internal class AiGeneratedSlot
{
    public int SlotIndex { get; set; }

    public int? SpanSlots { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public Guid? Id { get; set; }

    public Guid? ItemId { get; set; }

    public string? Title { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public int? Year { get; set; }

    public List<AiGeneratedCandidate>? Candidates { get; set; }
}

internal class AiGeneratedCandidate
{
    public Guid? JellyfinItemId { get; set; }

    public Guid? Id { get; set; }
}
