using System.Collections.Concurrent;
using FinTv.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class PlayoutBuilderService : BackgroundService
{
    private static readonly SemaphoreSlim ManualRebuildAllLock = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, ChannelPlayoutRebuildState> RebuildStates = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StreamService _stream;
    private readonly GuideUpdateTracker _guideUpdates;
    private readonly DatabaseInitializer _database;
    private readonly ILogger<PlayoutBuilderService> _logger;

    public PlayoutBuilderService(
        IServiceScopeFactory scopeFactory,
        StreamService stream,
        GuideUpdateTracker guideUpdates,
        DatabaseInitializer database,
        ILogger<PlayoutBuilderService> logger)
    {
        _scopeFactory = scopeFactory;
        _stream = stream;
        _guideUpdates = guideUpdates;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _database.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

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
            using var gate = await ChannelApplyLocks.AcquireAsync(channel.Id, cancellationToken);
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

                    if (activeHoliday is not null && FinTvRuntime.Current?.Configuration.Ai.Enabled == true)
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
                                cancellationToken,
                                preview.WeeklyLineups);
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

            if (!hasCoverageNow)
            {
                var nextStart = await db.PlayoutItems
                    .Where(p => p.ChannelId == channel.Id && p.Start > now)
                    .Select(p => (DateTime?)p.Start)
                    .MinAsync(cancellationToken);

                if (nextStart is DateTime upcoming && upcoming < horizonEnd)
                {
                    _logger.LogInformation(
                        "Channel {Channel} has a gap until {NextStart:u}; leaving existing playout in place so the guide stays aligned",
                        channel.Name,
                        upcoming);
                }
                else
                {
                    var rebuildStart = PlayoutScheduleHelper.GetScheduleDayStartUtc(now);
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
            }

            if (latestFinish.HasValue && latestFinish.Value >= horizonEnd)
            {
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var aiManaged = FinTvRuntime.Current?.Configuration.Ai.Enabled == true
                && AiChannelAutoApplyService.IsEligible(channel);
            if (aiManaged && latestFinish.HasValue && latestFinish.Value > now)
            {
                await db.SaveChangesAsync(cancellationToken);
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
        var rebuildStart = PlayoutScheduleHelper.GetScheduleDayStartUtc(now);
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);

        var channels = await db.Channels.Where(c => c.Enabled).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            using var gate = await ChannelApplyLocks.AcquireAsync(channel.Id, cancellationToken);
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

    /// <summary>
    /// Replaces the playout window for one channel from today through the horizon,
    /// saving one schedule day at a time so the TV guide can refresh before the full horizon finishes.
    /// </summary>
    public async Task RebuildChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        using var gate = await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var now = DateTime.UtcNow;
        var start = PlayoutScheduleHelper.GetScheduleDayStartUtc(now);
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);

        if (channel.IsContinuousLive)
        {
            await generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
        }
        else
        {
            var days = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            for (var day = 0; day < days; day++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                db.ChangeTracker.Clear();
                channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
                    ?? throw new InvalidOperationException("Channel not found.");

                var dayStart = PlayoutScheduleHelper.GetScheduleDayStartUtc(now, day);
                var dayEnd = PlayoutScheduleHelper.GetScheduleDayStartUtc(now, day + 1);
                await generator.BuildPlayoutAsync(
                    channel,
                    dayStart,
                    dayEnd,
                    PlayoutBuildMode.ReplaceWindow,
                    cancellationToken,
                    interruptStream: day == 0);

                _logger.LogInformation(
                    "Rebuilt playout day {Day}/{Days} for channel {Channel} ({Start:u} to {End:u})",
                    day + 1,
                    days,
                    channel.Name,
                    dayStart,
                    dayEnd);
            }
        }

        db.ChangeTracker.Clear();
        var itemCount = await db.PlayoutItems.CountAsync(
            p => p.ChannelId == channelId && p.Finish > now,
            cancellationToken);
        var hasCoverageNow = await db.PlayoutItems.AnyAsync(
            p => p.ChannelId == channelId && p.Start <= now && p.Finish > now,
            cancellationToken);

        _logger.LogInformation(
            "Rebuilt playout for channel {Channel} from {Start} to {End}: {ItemCount} future items, on-air now={HasCoverageNow}",
            channel.Name,
            start,
            end,
            itemCount,
            hasCoverageNow);
    }

    /// <summary>
    /// Gets the latest background rebuild status for a channel, if any.
    /// </summary>
    public ChannelPlayoutRebuildState? GetRebuildState(Guid channelId)
    {
        return RebuildStates.TryGetValue(channelId, out var state) ? state : null;
    }

    /// <summary>
    /// Queues a background playout rebuild so the admin HTTP request returns immediately.
    /// </summary>
    public void QueueRebuildChannel(Guid channelId)
    {
        var startedAt = MarkRebuildQueued(channelId);
        _ = Task.Run(async () =>
        {
            try
            {
                await RebuildChannelAndTrackAsync(channelId, startedAt).ConfigureAwait(false);
            }
            catch
            {
                // Failure is recorded on RebuildStates.
            }
        });
    }

    /// <summary>
    /// Rebuilds one channel and waits until the guide window is persisted.
    /// </summary>
    public Task RebuildChannelAndTrackAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var startedAt = MarkRebuildQueued(channelId);
        return RebuildChannelAndTrackAsync(channelId, startedAt, cancellationToken);
    }

    private DateTime MarkRebuildQueued(Guid channelId)
    {
        _logger.LogInformation("Queueing background playout rebuild for channel {ChannelId}", channelId);
        var startedAt = DateTime.UtcNow;
        RebuildStates[channelId] = new ChannelPlayoutRebuildState
        {
            State = "queued",
            StartedAtUtc = startedAt
        };
        return startedAt;
    }

    private async Task RebuildChannelAndTrackAsync(Guid channelId, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        RebuildStates[channelId] = new ChannelPlayoutRebuildState
        {
            State = "running",
            StartedAtUtc = startedAt
        };

        try
        {
            await RebuildChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
            await UpdateRebuildStateAfterSuccessAsync(channelId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background playout rebuild failed for channel {ChannelId}", channelId);
            RebuildStates[channelId] = new ChannelPlayoutRebuildState
            {
                State = "failed",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
                Error = ex.Message
            };
            throw;
        }
    }

    private async Task UpdateRebuildStateAfterSuccessAsync(Guid channelId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var now = DateTime.UtcNow;
        var playoutItemCount = await db.PlayoutItems.CountAsync(p => p.ChannelId == channelId && p.Finish > now)
            .ConfigureAwait(false);
        var hasCoverageNow = await db.PlayoutItems.AnyAsync(
                p => p.ChannelId == channelId && p.Start <= now && p.Finish > now)
            .ConfigureAwait(false);

        RebuildStates[channelId] = new ChannelPlayoutRebuildState
        {
            State = "completed",
            FinishedAtUtc = DateTime.UtcNow,
            PlayoutItemCount = playoutItemCount,
            HasCoverageNow = hasCoverageNow
        };
    }

    /// <summary>
    /// Deletes all playout items and resets episode cursors so the next rebuild starts fresh.
    /// </summary>
    public async Task<int> ClearAllGuideDataAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();

        var cleared = await db.PlayoutItems.CountAsync(cancellationToken);
        await db.PlayoutItems.ExecuteDeleteAsync(cancellationToken);
        await db.Channels.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(channel => channel.LastPlayoutBuiltAt, (DateTime?)null)
                .SetProperty(channel => channel.PlayoutAnchorJson, "{}"),
            cancellationToken);

        _stream.InterruptAllCurrentItems();
        _guideUpdates.MarkUpdated();
        _logger.LogInformation("Cleared {Count} playout items so the Live TV guide can start fresh", cleared);
        return cleared;
    }

    /// <summary>
    /// Queues a background rebuild for every enabled channel.
    /// </summary>
    public void QueueForceRebuildAllChannels()
    {
        _logger.LogInformation("Queueing background rebuild-all for enabled channels.");
        _ = Task.Run(async () =>
        {
            try
            {
                await ManualRebuildAllLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ForceRebuildAllChannelsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    ManualRebuildAllLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background rebuild-all failed");
            }
        });
    }
}

/// <summary>
/// Background playout rebuild status for one channel.
/// </summary>
public sealed class ChannelPlayoutRebuildState
{
    /// <summary>
    /// Gets or sets the rebuild state: queued, running, completed, or failed.
    /// </summary>
    public string State { get; set; } = "idle";

    /// <summary>
    /// Gets or sets when the rebuild was queued or started.
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets when the rebuild finished.
    /// </summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets how many future playout items exist after rebuild.
    /// </summary>
    public int PlayoutItemCount { get; set; }

    /// <summary>
    /// Gets or sets whether a playout item covers the current time after rebuild.
    /// </summary>
    public bool HasCoverageNow { get; set; }

    /// <summary>
    /// Gets or sets the error message when <see cref="State"/> is failed.
    /// </summary>
    public string? Error { get; set; }
}
