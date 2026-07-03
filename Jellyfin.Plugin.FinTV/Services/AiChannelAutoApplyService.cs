using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Applies default AI channel settings and lineups when channels are added or bulk-updated.
/// </summary>
public class AiChannelAutoApplyService
{
    private readonly FinTvDbContext _db;
    private readonly AiLineupGeneratorService _generator;
    private readonly LineupGeneratorService _playoutGenerator;
    private readonly ILogger<AiChannelAutoApplyService> _logger;

    public AiChannelAutoApplyService(
        FinTvDbContext db,
        AiLineupGeneratorService generator,
        LineupGeneratorService playoutGenerator,
        ILogger<AiChannelAutoApplyService> logger)
    {
        _db = db;
        _generator = generator;
        _playoutGenerator = playoutGenerator;
        _logger = logger;
    }

    public static bool IsEligible(Channel channel)
    {
        if (channel.ContentType == ChannelContentType.Weather)
        {
            return false;
        }

        var tag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        return !ChannelAiRules.IsExcludedFromAi(tag);
    }

    public async Task ApplyDefaultSettingsAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        var tag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        var rule = ChannelAiRules.GetByLibraryTag(tag);
        if (rule is not null)
        {
            channel.CatalogMode = rule.DefaultCatalogMode;
        }
        else if (!channel.CatalogMode.HasValue)
        {
            channel.CatalogMode = ChannelAiRules.ResolveCatalogMode(channel, tag);
        }

        var templateId = ChannelAiRules.GetDefaultPlayoutTemplateId(tag);
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            channel.AiPlayoutTemplateId = templateId;
        }
        else if (string.IsNullOrWhiteSpace(channel.AiPlayoutTemplateId))
        {
            channel.AiPlayoutTemplateId = AiPlayoutTemplates.NoneId;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiAutoApplyChannelResult> TryAutoApplyForChannelAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled || !settings.AutoApplyOnChannelAdd)
        {
            return AiAutoApplyChannelResult.Skipped("AI auto-apply is disabled.");
        }

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return AiAutoApplyChannelResult.Failed("Channel not found.");
        }

        if (!IsEligible(channel))
        {
            return AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups.");
        }

        try
        {
            await ApplyDefaultSettingsAsync(channel, cancellationToken);
            var preview = await _generator.GenerateAsync(channel.Id, null, cancellationToken);
            await _generator.ApplyAsync(
                channel.Id,
                preview.LineupSlots,
                rebuildPlayout: true,
                _playoutGenerator,
                cancellationToken);

            _logger.LogInformation(
                "Auto-applied AI lineup for channel {ChannelNumber} {ChannelName} with 14-day playout rebuild.",
                channel.Number,
                channel.Name);

            return AiAutoApplyChannelResult.Succeeded(channel.Id, channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI auto-apply failed for channel {ChannelId}", channelId);
            return AiAutoApplyChannelResult.Failed(ex.Message);
        }
    }

    public async Task<IReadOnlyList<AiAutoApplyChannelResult>> ApplyToAllEligibleChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            throw new InvalidOperationException("AI lineup generation is disabled.");
        }

        var channels = await _db.Channels
            .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var results = new List<AiAutoApplyChannelResult>();
        foreach (var channel in channels)
        {
            if (!IsEligible(channel))
            {
                results.Add(AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups."));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ApplyDefaultSettingsAsync(channel, cancellationToken);
                var preview = await _generator.GenerateAsync(channel.Id, null, cancellationToken);
                await _generator.ApplyAsync(
                    channel.Id,
                    preview.LineupSlots,
                    rebuildPlayout: true,
                    _playoutGenerator,
                    cancellationToken);
                results.Add(AiAutoApplyChannelResult.Succeeded(channel.Id, channel.Name));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI apply-all failed for channel {ChannelId}", channel.Id);
                results.Add(AiAutoApplyChannelResult.Failed(ex.Message, channel.Id, channel.Name));
            }
        }

        return results;
    }
}

public class AiAutoApplyChannelResult
{
    public Guid? ChannelId { get; set; }

    public string? ChannelName { get; set; }

    public bool Ok { get; set; }

    public bool WasSkipped { get; set; }

    public string? Error { get; set; }

    public static AiAutoApplyChannelResult Succeeded(Guid channelId, string channelName)
        => new() { Ok = true, ChannelId = channelId, ChannelName = channelName };

    public static AiAutoApplyChannelResult Skipped(string reason)
        => new() { WasSkipped = true, Error = reason };

    public static AiAutoApplyChannelResult Failed(string error, Guid? channelId = null, string? channelName = null)
        => new() { Ok = false, Error = error, ChannelId = channelId, ChannelName = channelName };
}
