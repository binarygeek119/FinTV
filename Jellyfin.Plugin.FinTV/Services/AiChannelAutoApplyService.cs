using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Applies default AI channel settings and lineups when channels are added or bulk-updated.
/// </summary>
public class AiChannelAutoApplyService
{
    private static readonly SemaphoreSlim BulkApplyLock = new(1, 1);

    private readonly FinTvDbContext _db;
    private readonly AiLineupGeneratorService _generator;
    private readonly LineupGeneratorService _playoutGenerator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiChannelAutoApplyService> _logger;

    public AiChannelAutoApplyService(
        FinTvDbContext db,
        AiLineupGeneratorService generator,
        LineupGeneratorService playoutGenerator,
        IServiceScopeFactory scopeFactory,
        ILogger<AiChannelAutoApplyService> logger)
    {
        _db = db;
        _generator = generator;
        _playoutGenerator = playoutGenerator;
        _scopeFactory = scopeFactory;
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

    public async Task ApplyDefaultSettingsAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

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

        var result = await ApplyChannelLineupAsync(channelId, cancellationToken);
        if (result.Ok)
        {
            _logger.LogInformation(
                "Auto-applied AI lineup for channel {ChannelName} with 14-day playout rebuild.",
                result.ChannelName);
        }

        return result;
    }

    public async Task<AiAutoApplyChannelResult> ApplyChannelLineupAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken);
        var channel = await _db.Channels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return AiAutoApplyChannelResult.Failed("Channel not found.", channelId);
        }

        if (!IsEligible(channel))
        {
            return AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups.");
        }

        try
        {
            await ApplyDefaultSettingsAsync(channelId, cancellationToken);
            _db.ChangeTracker.Clear();

            var preview = await _generator.GenerateAsync(channelId, null, cancellationToken);
            _db.ChangeTracker.Clear();

            await _generator.ApplyAsync(
                channelId,
                preview.LineupSlots,
                rebuildPlayout: true,
                _playoutGenerator,
                cancellationToken);

            return AiAutoApplyChannelResult.Succeeded(channelId, channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI lineup apply failed for channel {ChannelId}", channelId);
            return AiAutoApplyChannelResult.Failed(ex.Message, channelId, channel.Name);
        }
    }

    /// <summary>
    /// Runs AI auto-apply in the background so channel create/preset APIs return immediately.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    public void QueueAutoApplyForChannel(Guid channelId)
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled || !settings.AutoApplyOnChannelAdd)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                await service.TryAutoApplyForChannelAsync(channelId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background AI auto-apply failed for channel {ChannelId}", channelId);
            }
        });
    }

    /// <summary>
    /// Applies AI lineups to all eligible channels in the background after settings are saved.
    /// </summary>
    public void QueueApplyToAllEligibleChannels()
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled || !settings.AutoApplyToAllChannelsOnSave)
        {
            return;
        }

        _logger.LogInformation("Queueing AI apply-to-all for eligible channels.");
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                var results = await service.ApplyToAllEligibleChannelsAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation(
                    "Background AI apply-to-all finished: {Ok} ok, {Failed} failed, {Skipped} skipped.",
                    results.Count(r => r.Ok),
                    results.Count(r => !r.Ok && !r.WasSkipped),
                    results.Count(r => r.WasSkipped));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background AI apply-to-all failed.");
            }
        });
    }

    public async Task<IReadOnlyList<AiAutoApplyChannelResult>> ApplyToAllEligibleChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            throw new InvalidOperationException("AI lineup generation is disabled.");
        }

        await BulkApplyLock.WaitAsync(cancellationToken);
        try
        {
            var channels = await _db.Channels
                .AsNoTracking()
                .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather)
                .OrderBy(c => c.Number)
                .Select(c => new { c.Id, c.Name, c.FilterJson, c.ContentType })
                .ToListAsync(cancellationToken);

            var results = new List<AiAutoApplyChannelResult>();
            foreach (var channel in channels)
            {
                if (!IsEligible(new Channel { FilterJson = channel.FilterJson, ContentType = channel.ContentType }))
                {
                    results.Add(AiAutoApplyChannelResult.Skipped($"{channel.Name} is not eligible for AI lineups."));
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                results.Add(await service.ApplyChannelLineupAsync(channel.Id, cancellationToken));
            }

            return results;
        }
        finally
        {
            BulkApplyLock.Release();
        }
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
