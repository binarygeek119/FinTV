using System.Text.Json;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class AiLineupGeneratorService
{
    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;

    private readonly FinTvDbContext _db;
    private readonly AiCatalogManifestBuilder _manifestBuilder;
    private readonly LlmClientService _llm;
    private readonly JellyfinCatalogService _catalog;
    private readonly LineupService _lineups;
    private readonly HolidayChannelService _holidays;

    public AiLineupGeneratorService(
        FinTvDbContext db,
        AiCatalogManifestBuilder manifestBuilder,
        LlmClientService llm,
        JellyfinCatalogService catalog,
        LineupService lineups,
        HolidayChannelService holidays)
    {
        _db = db;
        _manifestBuilder = manifestBuilder;
        _llm = llm;
        _catalog = catalog;
        _lineups = lineups;
        _holidays = holidays;
    }

    public async Task<AiLineupPreviewResult> GenerateAsync(
        Guid channelId,
        AiProvider? providerOverride,
        CancellationToken cancellationToken = default)
    {
        EnsureAiEnabled();

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        if (channel.ContentType == ChannelContentType.Weather)
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
        var provider = providerOverride ?? Plugin.Instance?.Configuration.Ai.DefaultProvider ?? AiProvider.OpenAi;

        var systemPrompt = BuildSystemPrompt(catalogMode, playoutTemplate);
        var userPrompt = BuildUserPrompt(channel, manifest, ruleBrief, catalogMode, playoutTemplate);

        var rawJson = await _llm.CompleteJsonAsync(provider, systemPrompt, userPrompt, cancellationToken);
        var aiResponse = ParseAiResponse(rawJson);

        var validIds = manifest.Catalog.Select(c => c.Id).ToHashSet();
        var catalogById = manifest.Catalog.ToDictionary(c => c.Id);
        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        var slots = ValidateAndBuildSlots(
            aiResponse.Slots,
            validIds,
            catalogById,
            channel.FilterJson,
            yearConstraints,
            playoutTemplate,
            catalogMode);

        return BuildPreview(channel, slots, manifest, provider, playoutTemplate);
    }

    public async Task ApplyAsync(
        Guid channelId,
        IReadOnlyList<LineupSlotDto> slots,
        bool rebuildPlayout,
        LineupGeneratorService generator,
        CancellationToken cancellationToken = default)
    {
        EnsureAiEnabled();
        await _lineups.UpdateDefaultSlotsAsync(channelId, NormalizeSlots(slots), cancellationToken);
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
            await generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Invalid schedule time zone in FinTV settings. Set Dashboard → Plugins → FinTV → schedule time zone to a valid IANA id (e.g. America/New_York).",
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
        AiPlayoutTemplate playoutTemplate)
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
            LineupSlots = slots
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

        return "No matching content found in your Jellyfin library for this channel. Ensure items have genres, release years, and ratings metadata.";
    }

    private static List<LineupSlotDto> ValidateAndBuildSlots(
        List<AiGeneratedSlot>? aiSlots,
        HashSet<Guid> validIds,
        Dictionary<Guid, AiCatalogEntry> catalogById,
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

            var candidateId = aiSlot.JellyfinItemId ?? aiSlot.Candidates?.FirstOrDefault()?.JellyfinItemId;
            if (!candidateId.HasValue || !validIds.Contains(candidateId.Value))
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

                span = entry.RuntimeMinutes > 0
                    ? ComputeSpanFromRuntime(entry.RuntimeMinutes, GetMaxSpanSlots(playoutTemplate))
                    : Math.Clamp(aiSlot.SpanSlots ?? 1, 1, GetMaxSpanSlots(playoutTemplate));
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

        FillEmptySlotsFromCatalog(result, occupied, catalogById);
        return FillEmptySlotsWithFilterFallback(result, occupied, channelFilterJson);
    }

    private static bool ShouldPackMarathon(AiPlayoutTemplate? template, ChannelCatalogMode catalogMode)
        => template?.Id is "movie-marathon" or "holiday-channel"
            || catalogMode == ChannelCatalogMode.MovieOnly;

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

        FillEmptySlotsFromCatalog(result, occupied, catalogById);
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
        Dictionary<Guid, AiCatalogEntry> catalogById)
    {
        if (catalogById.Count == 0)
        {
            return result.Values.OrderBy(s => s.SlotIndex).ToList();
        }

        var fillQueue = catalogById.Values
            .OrderBy(e => e.Year ?? int.MaxValue)
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

    private static string BuildSystemPrompt(ChannelCatalogMode catalogMode, AiPlayoutTemplate playoutTemplate)
    {
        var templateSection = AiPlayoutTemplates.BuildPromptSection(playoutTemplate);
        var templateBlock = string.IsNullOrWhiteSpace(templateSection)
            ? string.Empty
            : "\n" + templateSection;

        return """
            You are a TV channel scheduling assistant for FinTV.
            Build a 48-slot daily lineup (each base slot is 30 minutes, slotIndex 0 = midnight).
            Reply with JSON only using this shape:
            {"slots":[{"slotIndex":0,"spanSlots":1,"jellyfinItemId":"guid"}, ...]}
            Rules:
            - Only use jellyfinItemId values from the provided catalog
            - spanSlots = number of consecutive 30-minute blocks (1-8). Use ceil(runtimeMinutes / 30) for movies and long episodes.
            - Assign enough spanSlots to cover the item runtime; under-sized spans truncate content during playout.
            - Do not overlap spans. slotIndex + spanSlots must be <= 48.
            - Group TV series into consecutive episode blocks (same jellyfinItemId in back-to-back slots); typical blocks are 1-4 episodes, with one 5-6 episode mini-marathon in a flagship daypart.
            - Vary series across the day; switch shows between blocks instead of isolating single random episodes.
            - Schedule movies in release chronological order using catalog year and premiere date (earliest first).
            - Catalog modes: TvOnly (series), MovieOnly, Mixed (TV+movies), MusicVideoOnly (music videos).
            """ + $"\nCatalog mode: {catalogMode}." + templateBlock;
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
            catalog = manifest.Catalog.Select(c => new
            {
                id = c.Id,
                title = c.Title,
                type = c.Type,
                year = c.Year,
                runtimeMinutes = c.RuntimeMinutes,
                genres = c.Genres,
                tags = c.Tags,
                plot = c.Plot
            }),
            totalAvailable = manifest.TotalAvailable
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static void EnsureAiEnabled()
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
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
}

internal class AiGeneratedSlot
{
    public int SlotIndex { get; set; }

    public int? SpanSlots { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public List<AiGeneratedCandidate>? Candidates { get; set; }
}

internal class AiGeneratedCandidate
{
    public Guid? JellyfinItemId { get; set; }
}
