using Jellyfin.Plugin.FinTV.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class PlayoutBuilderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlayoutBuilderService> _logger;

    public PlayoutBuilderService(IServiceScopeFactory scopeFactory, ILogger<PlayoutBuilderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BuildAllChannelsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playout builder failed");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    public async Task BuildAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();
        var commercialService = scope.ServiceProvider.GetRequiredService<CommercialService>();
        var channelService = scope.ServiceProvider.GetRequiredService<ChannelService>();
        var holidays = scope.ServiceProvider.GetRequiredService<HolidayChannelService>();

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await commercialService.SyncCommercialLibraryAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);
        var trimBefore = now.AddDays(-2);

        var channels = await db.Channels.Where(c => c.Enabled).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            if (holidays.IsHolidayChannel(channel))
            {
                var scheduleDate = holidays.GetScheduleDateUtc(now);
                var activeHoliday = holidays.GetActiveHoliday(scheduleDate);
                var anchor = await channelService.GetAnchorAsync<PlayoutAnchorState>(channel.Id, cancellationToken)
                    ?? new PlayoutAnchorState();
                var activeId = activeHoliday?.Id;
                if (!string.Equals(anchor.LastHolidayId, activeId, StringComparison.Ordinal))
                {
                    anchor.LastHolidayId = activeId;
                    await channelService.SaveAnchorAsync(channel.Id, anchor, cancellationToken);

                    if (activeHoliday is not null && Plugin.Instance?.Configuration.Ai.Enabled == true)
                    {
                        try
                        {
                            var ai = scope.ServiceProvider.GetRequiredService<AiLineupGeneratorService>();
                            var preview = await ai.GenerateAsync(channel.Id, null, cancellationToken);
                            await ai.ApplyAsync(
                                channel.Id,
                                preview.LineupSlots,
                                rebuildPlayout: false,
                                generator,
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Holiday AI lineup refresh failed for {Channel}", channel.Name);
                        }
                    }

                    await generator.BuildPlayoutAsync(
                        channel,
                        now,
                        horizonEnd,
                        PlayoutBuildMode.ReplaceWindow,
                        cancellationToken);

                    _logger.LogInformation(
                        "Holiday season changed for {Channel} to {Holiday}; rebuilt playout",
                        channel.Name,
                        activeHoliday?.Name ?? "off-season");
                    continue;
                }
            }

            var stale = await db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish < trimBefore)
                .ToListAsync(cancellationToken);
            if (stale.Count > 0)
            {
                db.PlayoutItems.RemoveRange(stale);
            }

            var latestFinish = await db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish > now)
                .Select(p => (DateTime?)p.Finish)
                .MaxAsync(cancellationToken);

            var hasCoverageNow = await db.PlayoutItems
                .AnyAsync(p => p.ChannelId == channel.Id && p.Start <= now && p.Finish > now, cancellationToken);

            if (latestFinish.HasValue && latestFinish.Value >= horizonEnd && hasCoverageNow)
            {
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (!hasCoverageNow)
            {
                var rebuildStart = now.Date;
                await generator.BuildPlayoutAsync(
                    channel,
                    rebuildStart,
                    horizonEnd,
                    PlayoutBuildMode.ReplaceWindow,
                    cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Rebuilt playout for channel {Channel} from {Start} to {End} (no current coverage)",
                    channel.Name,
                    rebuildStart,
                    horizonEnd);
                continue;
            }

            var appendStart = latestFinish ?? now;
            if (appendStart < now)
            {
                appendStart = now;
            }

            await generator.BuildPlayoutAsync(
                channel,
                appendStart,
                horizonEnd,
                PlayoutBuildMode.ExtendHorizon,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Extended playout for channel {Channel} from {Start} to {End}",
                channel.Name,
                appendStart,
                horizonEnd);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the playout window for every enabled channel from today through the horizon.
    /// Used by the admin Rebuild All action (not the hourly maintenance loop).
    /// </summary>
    public async Task ForceRebuildAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();
        var commercialService = scope.ServiceProvider.GetRequiredService<CommercialService>();

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await commercialService.SyncCommercialLibraryAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var rebuildStart = now.Date;
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);

        var channels = await db.Channels.Where(c => c.Enabled).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            await generator.BuildPlayoutAsync(
                channel,
                rebuildStart,
                horizonEnd,
                PlayoutBuildMode.ReplaceWindow,
                cancellationToken);

            _logger.LogInformation(
                "Force rebuilt playout for channel {Channel} from {Start} to {End}",
                channel.Name,
                rebuildStart,
                horizonEnd);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
