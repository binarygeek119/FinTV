using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class AiLineupGeneratorService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FinTvDbContext _db;
    private readonly AiCatalogManifestBuilder _manifestBuilder;
    private readonly LlmClientService _llm;
    private readonly JellyfinCatalogService _catalog;
    private readonly LineupService _lineups;

    public AiLineupGeneratorService(
        FinTvDbContext db,
        AiCatalogManifestBuilder manifestBuilder,
        LlmClientService llm,
        JellyfinCatalogService catalog,
        LineupService lineups)
    {
        _db = db;
        _manifestBuilder = manifestBuilder;
        _llm = llm;
        _catalog = catalog;
        _lineups = lineups;
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

        var manifest = _manifestBuilder.Build(channel);
        if (manifest.Catalog.Count == 0)
        {
            throw new InvalidOperationException("No tagged shows/movies found for this channel — tag your library first.");
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
        var slots = ValidateAndBuildSlots(aiResponse.Slots, validIds, catalogById, channel.FilterJson);

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

        if (!rebuildPlayout)
        {
            return;
        }

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var start = DateTime.UtcNow.Date;
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
        await generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
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

    private static List<LineupSlotDto> ValidateAndBuildSlots(
        List<AiGeneratedSlot>? aiSlots,
        HashSet<Guid> validIds,
        Dictionary<Guid, AiCatalogEntry> catalogById,
        string? channelFilterJson)
    {
        var occupied = new bool[48];
        var result = ChannelService.CreateEmptySlots()
            .Select(s => new LineupSlotDto { SlotIndex = s.SlotIndex, SpanSlots = 1 })
            .ToDictionary(s => s.SlotIndex);

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
                span = Math.Clamp(
                    aiSlot.SpanSlots ?? ComputeSpanFromRuntime(entry.RuntimeMinutes),
                    1,
                    8);
                if (aiSlot.SlotIndex + span > 48)
                {
                    span = 48 - aiSlot.SlotIndex;
                }

                if (IsRangeOccupied(occupied, aiSlot.SlotIndex, span))
                {
                    continue;
                }
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
        }

        var fallbackFilter = string.IsNullOrWhiteSpace(channelFilterJson)
            ? "{\"tags\":[]}"
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

    private static int ComputeSpanFromRuntime(int runtimeMinutes)
        => Math.Clamp((int)Math.Ceiling(runtimeMinutes / 30.0), 1, 8);

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
                Candidates = slot.Candidates
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
            - spanSlots = number of consecutive 30-minute blocks (1-8). Use longer spans for movies.
            - Do not overlap spans. slotIndex + spanSlots must be <= 48.
            - Vary content across dayparts; avoid repeating the same title in adjacent blocks.
            """ + $"\nCatalog mode: {catalogMode}." + templateBlock;
    }

    private static string BuildUserPrompt(
        Channel channel,
        AiCatalogManifest manifest,
        string ruleBrief,
        ChannelCatalogMode catalogMode,
        AiPlayoutTemplate playoutTemplate)
    {
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
                tags = c.Tags
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
