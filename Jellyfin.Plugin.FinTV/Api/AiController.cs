using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// AI lineup generation settings and actions.
/// </summary>
[ApiController]
[Route("FinTV/api/ai")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AiController : ControllerBase
{
    private readonly AiLineupGeneratorService _generator;
    private readonly AiChannelAutoApplyService _autoApply;
    private readonly LlmClientService _llm;
    private readonly FinTvDbContext _db;
    private readonly LineupGeneratorService _playoutGenerator;
    private readonly WeatherGuideMetadataService _weatherGuide;

    public AiController(
        AiLineupGeneratorService generator,
        AiChannelAutoApplyService autoApply,
        LlmClientService llm,
        FinTvDbContext db,
        LineupGeneratorService playoutGenerator,
        WeatherGuideMetadataService weatherGuide)
    {
        _generator = generator;
        _autoApply = autoApply;
        _llm = llm;
        _db = db;
        _playoutGenerator = playoutGenerator;
        _weatherGuide = weatherGuide;
    }

    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
        => Ok(BuildSettingsResponse());

    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] AiSettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
        var ai = plugin.Configuration.Ai;

        if (request.Enabled.HasValue)
        {
            ai.Enabled = request.Enabled.Value;
        }

        if (request.DefaultProvider.HasValue)
        {
            ai.DefaultProvider = request.DefaultProvider.Value;
        }

        if (request.OpenAiModel is not null)
        {
            ai.OpenAiModel = request.OpenAiModel;
        }

        if (request.VeniceModel is not null)
        {
            ai.VeniceModel = request.VeniceModel;
        }

        if (request.MaxCatalogItemsInPrompt.HasValue)
        {
            ai.MaxCatalogItemsInPrompt = Math.Clamp(request.MaxCatalogItemsInPrompt.Value, 10, 1000);
        }

        if (request.AutoApplyOnChannelAdd.HasValue)
        {
            ai.AutoApplyOnChannelAdd = request.AutoApplyOnChannelAdd.Value;
        }

        if (request.AutoApplyToAllChannelsOnSave.HasValue)
        {
            ai.AutoApplyToAllChannelsOnSave = request.AutoApplyToAllChannelsOnSave.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.OpenAiApiKey))
        {
            ai.OpenAiApiKey = request.OpenAiApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.VeniceApiKey))
        {
            ai.VeniceApiKey = request.VeniceApiKey.Trim();
        }

        plugin.SaveConfiguration();

        object? applyAllSummary = null;
        if (ai.Enabled && ai.AutoApplyToAllChannelsOnSave)
        {
            _autoApply.QueueApplyToAllEligibleChannels();
            applyAllSummary = new { queued = true };
        }

        return Ok(new
        {
            settings = BuildSettingsResponse(),
            applyAll = applyAllSummary
        });
    }

    private static object BuildSettingsResponse()
    {
        var ai = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        return new
        {
            enabled = ai.Enabled,
            defaultProvider = (int)ai.DefaultProvider,
            openAiModel = ai.OpenAiModel,
            veniceModel = ai.VeniceModel,
            maxCatalogItemsInPrompt = ai.MaxCatalogItemsInPrompt,
            hasOpenAiApiKey = !string.IsNullOrWhiteSpace(ai.OpenAiApiKey),
            hasVeniceApiKey = !string.IsNullOrWhiteSpace(ai.VeniceApiKey),
            openAiApiKeyMasked = MaskKey(ai.OpenAiApiKey),
            veniceApiKeyMasked = MaskKey(ai.VeniceApiKey),
            autoApplyOnChannelAdd = ai.AutoApplyOnChannelAdd,
            autoApplyToAllChannelsOnSave = ai.AutoApplyToAllChannelsOnSave
        };
    }

    [HttpPost("settings/test")]
    public async Task<IActionResult> TestSettings([FromBody] AiTestSettingsRequest? request, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
        var provider = request?.Provider ?? plugin.Configuration.Ai.DefaultProvider;

        try
        {
            await _llm.TestConnectionAsync(
                provider,
                request?.OpenAiApiKey,
                request?.VeniceApiKey,
                cancellationToken);
            return Ok(new { ok = true, provider = provider.ToString() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Connection test failed: {ex.Message}" });
        }
    }

    [HttpGet("channels")]
    public async Task<ActionResult<object>> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await _db.Channels
            .AsNoTracking()
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var slotCounts = await _db.Lineups
            .Where(l => l.IsDefault)
            .Select(l => new { l.ChannelId, Count = l.Slots.Count(s => s.Candidates.Count > 0) })
            .ToDictionaryAsync(x => x.ChannelId, x => x.Count, cancellationToken);

        return Ok(channels
            .Where(c => c.ContentType != ChannelContentType.Weather)
            .Where(c => !ChannelAiRules.IsExcludedFromAi(ChannelAiRules.ExtractLibraryTag(c.FilterJson)))
            .Select(c =>
            {
                var tag = ChannelAiRules.ExtractLibraryTag(c.FilterJson);
                return new
                {
                    id = c.Id,
                    number = c.Number,
                    name = c.Name,
                    libraryTag = tag,
                    catalogMode = (int)ChannelAiRules.ResolveCatalogMode(c),
                    aiFineTunePrompt = c.AiFineTunePrompt ?? string.Empty,
                    aiPlayoutTemplateId = c.AiPlayoutTemplateId ?? AiPlayoutTemplates.NoneId,
                    aiRuleBrief = ChannelAiRules.GetBrief(tag),
                    filledSlots = slotCounts.TryGetValue(c.Id, out var count) ? count : 0
                };
            }));
    }

    [HttpGet("playout-templates")]
    public ActionResult<object> GetPlayoutTemplates()
    {
        return Ok(AiPlayoutTemplates.ListAll().Select(t => new
        {
            id = t.Id,
            name = t.Name,
            description = t.Description,
            dayparts = t.Dayparts.Select(d => new
            {
                name = d.Name,
                slotRange = d.FormatSlotRange(),
                brief = d.Brief
            })
        }));
    }

    [HttpPut("channels/{channelId:guid}/playout-template")]
    public async Task<IActionResult> UpdatePlayoutTemplate(
        Guid channelId,
        [FromBody] AiPlayoutTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        channel.AiPlayoutTemplateId = string.IsNullOrWhiteSpace(request.AiPlayoutTemplateId)
            ? AiPlayoutTemplates.NoneId
            : request.AiPlayoutTemplateId.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { ok = true });
    }

    [HttpPut("channels/{channelId:guid}/fine-tune")]
    public async Task<IActionResult> UpdateFineTune(
        Guid channelId,
        [FromBody] AiChannelSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (request.AiFineTunePrompt is not null)
        {
            channel.AiFineTunePrompt = request.AiFineTunePrompt;
        }

        if (request.CatalogMode.HasValue)
        {
            channel.CatalogMode = request.CatalogMode.Value;
        }

        if (request.AiPlayoutTemplateId is not null)
        {
            channel.AiPlayoutTemplateId = string.IsNullOrWhiteSpace(request.AiPlayoutTemplateId)
                ? AiPlayoutTemplates.NoneId
                : request.AiPlayoutTemplateId.Trim();
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { ok = true });
    }

    [HttpPost("channels/{channelId:guid}/generate")]
    public async Task<ActionResult<AiLineupPreviewResult>> Generate(
        Guid channelId,
        [FromBody] AiGenerateRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var preview = await _generator.GenerateAsync(channelId, request?.Provider, cancellationToken);
            return Ok(preview);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("channels/{channelId:guid}/apply")]
    public async Task<IActionResult> Apply(
        Guid channelId,
        [FromBody] AiApplyLineupRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var gate = await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken);
            await _generator.ApplyAsync(
                channelId,
                request?.Slots ?? new List<LineupSlotDto>(),
                request?.RebuildPlayout ?? true,
                _playoutGenerator,
                cancellationToken);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("generate-all")]
    public ActionResult<object> GenerateAll()
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            return BadRequest(new { message = "AI lineup generation is disabled." });
        }

        if (_autoApply.IsGenerateAllJobRunning)
        {
            return Ok(new { queued = false, alreadyRunning = true, job = _autoApply.BuildGenerateAllStatus() });
        }

        _autoApply.QueueManualGenerateAllEligibleChannels();
        return Ok(new { queued = true, job = _autoApply.BuildGenerateAllStatus() });
    }

    [HttpGet("generate-all/status")]
    public ActionResult<object> GetGenerateAllStatus()
        => Ok(_autoApply.BuildGenerateAllStatus());

    [HttpPost("generate-all/cancel")]
    public ActionResult<object> CancelGenerateAll()
    {
        var cancelled = _autoApply.CancelGenerateAll();
        return Ok(new { cancelled, job = _autoApply.BuildGenerateAllStatus() });
    }

    [HttpGet("weather-guide-cache/status")]
    public async Task<ActionResult<object>> GetWeatherGuideCacheStatus(CancellationToken cancellationToken)
        => Ok(await _weatherGuide.BuildCacheStatusAsync(cancellationToken));

    [HttpPost("weather-guide-cache/generate")]
    public async Task<IActionResult> GenerateWeatherGuideCache(
        [FromBody] WeatherGuideCacheGenerateRequest? request,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            return BadRequest(new { message = "AI lineup generation is disabled." });
        }

        if (_weatherGuide.IsGenerating)
        {
            return Ok(new
            {
                queued = false,
                alreadyRunning = true,
                status = await _weatherGuide.BuildCacheStatusAsync(cancellationToken)
            });
        }

        try
        {
            _weatherGuide.QueueGenerateCache(request?.Force == true);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Accepted(new
        {
            queued = true,
            status = await _weatherGuide.BuildCacheStatusAsync(cancellationToken)
        });
    }

    [HttpDelete("weather-guide-cache")]
    public ActionResult<object> ClearWeatherGuideCache()
    {
        var cleared = _weatherGuide.ClearCache();
        return Ok(new { cleared });
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (key.Length <= 8)
        {
            return "****";
        }

        return key[..4] + "..." + key[^4..];
    }
}

public class AiSettingsRequest
{
    public bool? Enabled { get; set; }

    public AiProvider? DefaultProvider { get; set; }

    public string? OpenAiApiKey { get; set; }

    public string? OpenAiModel { get; set; }

    public string? VeniceApiKey { get; set; }

    public string? VeniceModel { get; set; }

    public int? MaxCatalogItemsInPrompt { get; set; }

    public bool? AutoApplyOnChannelAdd { get; set; }

    public bool? AutoApplyToAllChannelsOnSave { get; set; }
}

public class AiTestSettingsRequest
{
    public AiProvider? Provider { get; set; }

    public string? OpenAiApiKey { get; set; }

    public string? VeniceApiKey { get; set; }
}

public class AiChannelSettingsRequest
{
    public string? AiFineTunePrompt { get; set; }

    public ChannelCatalogMode? CatalogMode { get; set; }

    public string? AiPlayoutTemplateId { get; set; }
}

public class AiPlayoutTemplateRequest
{
    public string? AiPlayoutTemplateId { get; set; }
}

public class AiGenerateRequest
{
    public AiProvider? Provider { get; set; }
}

public class AiApplyLineupRequest
{
    public List<LineupSlotDto>? Slots { get; set; }

    public bool RebuildPlayout { get; set; }
}

public class WeatherGuideCacheGenerateRequest
{
    public bool Force { get; set; }
}
