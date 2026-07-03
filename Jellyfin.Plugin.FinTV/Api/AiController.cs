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
    private readonly LlmClientService _llm;
    private readonly FinTvDbContext _db;
    private readonly LineupGeneratorService _playoutGenerator;

    public AiController(
        AiLineupGeneratorService generator,
        LlmClientService llm,
        FinTvDbContext db,
        LineupGeneratorService playoutGenerator)
    {
        _generator = generator;
        _llm = llm;
        _db = db;
        _playoutGenerator = playoutGenerator;
    }

    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
    {
        var ai = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        return Ok(new
        {
            enabled = ai.Enabled,
            defaultProvider = (int)ai.DefaultProvider,
            openAiModel = ai.OpenAiModel,
            veniceModel = ai.VeniceModel,
            maxCatalogItemsInPrompt = ai.MaxCatalogItemsInPrompt,
            hasOpenAiApiKey = !string.IsNullOrWhiteSpace(ai.OpenAiApiKey),
            hasVeniceApiKey = !string.IsNullOrWhiteSpace(ai.VeniceApiKey),
            openAiApiKeyMasked = MaskKey(ai.OpenAiApiKey),
            veniceApiKeyMasked = MaskKey(ai.VeniceApiKey)
        });
    }

    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] AiSettingsRequest request)
    {
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

        if (!string.IsNullOrWhiteSpace(request.OpenAiApiKey))
        {
            ai.OpenAiApiKey = request.OpenAiApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.VeniceApiKey))
        {
            ai.VeniceApiKey = request.VeniceApiKey.Trim();
        }

        plugin.SaveConfiguration();
        return GetSettings();
    }

    [HttpPost("settings/test")]
    public async Task<IActionResult> TestSettings([FromBody] AiTestSettingsRequest request, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            return BadRequest(new { message = "AI lineup generation is disabled." });
        }

        var provider = request.Provider ?? Plugin.Instance.Configuration.Ai.DefaultProvider;
        await _llm.TestConnectionAsync(provider, cancellationToken);
        return Ok(new { ok = true, provider = provider.ToString() });
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
        return NoContent();
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
        return NoContent();
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
        [FromBody] AiApplyLineupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _generator.ApplyAsync(
                channelId,
                request.Slots ?? new List<LineupSlotDto>(),
                request.RebuildPlayout,
                _playoutGenerator,
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("generate-all")]
    public async Task<ActionResult<object>> GenerateAll(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            return BadRequest(new { message = "AI lineup generation is disabled." });
        }

        var channels = await _db.Channels
            .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var results = new List<object>();
        foreach (var channel in channels)
        {
            try
            {
                var preview = await _generator.GenerateAsync(channel.Id, null, cancellationToken);
                await _generator.ApplyAsync(channel.Id, preview.LineupSlots, true, _playoutGenerator, cancellationToken);
                results.Add(new { channelId = channel.Id, name = channel.Name, ok = true });
            }
            catch (Exception ex)
            {
                results.Add(new { channelId = channel.Id, name = channel.Name, ok = false, error = ex.Message });
            }
        }

        return Ok(new { results });
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
}

public class AiTestSettingsRequest
{
    public AiProvider? Provider { get; set; }
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
