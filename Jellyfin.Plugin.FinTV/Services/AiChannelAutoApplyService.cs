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
    private static readonly object PendingQueueLock = new();

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

        var result = await ApplyChannelLineupAsync(channelId, cancellationToken: cancellationToken);
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
        bool rebuildPlayout = true,
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
                rebuildPlayout: rebuildPlayout,
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

    public bool IsGenerateAllJobRunning =>
        Plugin.Instance?.Configuration.AiGenerateAllJob.IsRunning == true;

    /// <summary>
    /// Generates AI lineups and builds playout one channel and one day at a time until the full horizon is covered.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunStaggeredGenerateAllAsync(CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            throw new InvalidOperationException("AI lineup generation is disabled.");
        }

        await BulkApplyLock.WaitAsync(cancellationToken);
        var state = new AiGenerateAllJobState
        {
            IsRunning = true,
            StartedAt = DateTime.UtcNow,
            LastError = null,
            CompletedAt = null
        };

        try
        {
            var daysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            var channelRows = await _db.Channels
                .AsNoTracking()
                .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather)
                .OrderBy(c => c.Number)
                .Select(c => new { c.Id, c.Name, c.FilterJson, c.ContentType })
                .ToListAsync(cancellationToken);

            var eligibleChannels = channelRows
                .Where(c => IsEligible(new Channel { FilterJson = c.FilterJson, ContentType = c.ContentType }))
                .Select(c => (c.Id, c.Name))
                .ToList();

            state.TotalDays = daysToBuild;
            state.TotalChannels = eligibleChannels.Count;
            state.TotalSteps = eligibleChannels.Count * daysToBuild;
            SaveGenerateAllState(state);

            if (eligibleChannels.Count == 0)
            {
                state.LastError = "No eligible channels found for AI lineups.";
                return;
            }

            var lineupGenerated = new HashSet<Guid>();
            var excludedChannels = new HashSet<Guid>();
            var extendOnlyChannels = new HashSet<Guid>();

            foreach (var (channelId, channelName) in eligibleChannels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maintainResult = await MaintainPlayoutHorizonAsync(channelId, cancellationToken)
                    .ConfigureAwait(false);
                if (maintainResult is PlayoutHorizonMaintainResult.AlreadyAtHorizon
                    or PlayoutHorizonMaintainResult.ExtendedOneDay)
                {
                    extendOnlyChannels.Add(channelId);
                    if (maintainResult == PlayoutHorizonMaintainResult.ExtendedOneDay)
                    {
                        state.PlayoutDaysBuilt++;
                    }

                    state.CompletedSteps += daysToBuild;
                    SaveGenerateAllState(state);
                    if (maintainResult == PlayoutHorizonMaintainResult.ExtendedOneDay)
                    {
                        _logger.LogInformation(
                            "AI generate-all extended playout by one day for {ChannelName} (13-day horizon maintenance).",
                            channelName);
                    }
                }
            }

            for (var dayIndex = 0; dayIndex < daysToBuild; dayIndex++)
            {
                foreach (var (channelId, channelName) in eligibleChannels)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (extendOnlyChannels.Contains(channelId))
                    {
                        continue;
                    }

                    state.CurrentDay = dayIndex + 1;
                    state.CurrentChannelName = channelName;
                    SaveGenerateAllState(state);

                    if (excludedChannels.Contains(channelId))
                    {
                        state.CompletedSteps++;
                        SaveGenerateAllState(state);
                        continue;
                    }

                    if (!lineupGenerated.Contains(channelId))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                        var lineupResult = await service.ApplyChannelLineupAsync(
                            channelId,
                            rebuildPlayout: false,
                            cancellationToken).ConfigureAwait(false);

                        if (lineupResult.Ok)
                        {
                            lineupGenerated.Add(channelId);
                            state.LineupsGenerated++;
                        }
                        else if (!lineupResult.WasSkipped)
                        {
                            state.LineupsFailed++;
                            state.LastError = $"{channelName}: {lineupResult.Error}";
                            excludedChannels.Add(channelId);
                            _logger.LogWarning(
                                "AI lineup generation failed for {ChannelName}: {Error}",
                                channelName,
                                lineupResult.Error);
                            state.CompletedSteps++;
                            SaveGenerateAllState(state);
                            continue;
                        }
                        else
                        {
                            excludedChannels.Add(channelId);
                            state.CompletedSteps++;
                            SaveGenerateAllState(state);
                            continue;
                        }
                    }

                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                        await service.BuildChannelPlayoutDayAsync(channelId, dayIndex, cancellationToken)
                            .ConfigureAwait(false);
                        state.PlayoutDaysBuilt++;
                    }
                    catch (Exception ex)
                    {
                        state.PlayoutDaysFailed++;
                        state.LastError = $"{channelName} day {dayIndex + 1}: {ex.Message}";
                        _logger.LogWarning(
                            ex,
                            "AI generate-all playout build failed for {ChannelName} day {Day}",
                            channelName,
                            dayIndex + 1);
                    }

                    state.CompletedSteps++;
                    SaveGenerateAllState(state);
                }
            }

            _logger.LogInformation(
                "Staggered AI generate-all finished: {Lineups} lineups, {PlayoutDays} playout days built across {Channels} channels and {Days} days.",
                state.LineupsGenerated,
                state.PlayoutDaysBuilt,
                eligibleChannels.Count,
                daysToBuild);
        }
        finally
        {
            state.IsRunning = false;
            state.CurrentChannelName = null;
            state.CompletedAt = DateTime.UtcNow;
            SaveGenerateAllState(state);
            BulkApplyLock.Release();
        }
    }

    public async Task BuildChannelPlayoutDayAsync(
        Guid channelId,
        int dayOffset,
        CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var dayStart = DateTime.UtcNow.Date.AddDays(dayOffset);
        var dayEnd = dayStart.AddDays(1);
        await _playoutGenerator.BuildPlayoutAsync(
            channel,
            dayStart,
            dayEnd,
            PlayoutBuildMode.ReplaceWindow,
            cancellationToken);
    }

    /// <summary>
    /// Extends playout by up to one day when the schedule has rolled forward to a 13-day horizon.
    /// </summary>
    public async Task<PlayoutHorizonMaintainResult> MaintainPlayoutHorizonAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null || channel.ContentType == ChannelContentType.Weather || !IsEligible(channel))
        {
            return PlayoutHorizonMaintainResult.NeedsFullBuild;
        }

        if (!await HasDefaultLineupAsync(channelId, cancellationToken).ConfigureAwait(false))
        {
            return PlayoutHorizonMaintainResult.NeedsFullBuild;
        }

        var now = DateTime.UtcNow;
        var latestFinish = await GetLatestPlayoutFinishUtcAsync(channelId, now, cancellationToken)
            .ConfigureAwait(false);
        var status = PlayoutScheduleHelper.AnalyzeHorizon(now, latestFinish);

        if (status.IsAtHorizon)
        {
            return PlayoutHorizonMaintainResult.AlreadyAtHorizon;
        }

        if (!status.NeedsOneDayExtension || !status.LatestFinishUtc.HasValue)
        {
            return PlayoutHorizonMaintainResult.NeedsFullBuild;
        }

        await _playoutGenerator.BuildPlayoutAsync(
            channel,
            status.LatestFinishUtc.Value,
            status.HorizonEndUtc,
            PlayoutBuildMode.ExtendHorizon,
            cancellationToken).ConfigureAwait(false);

        return PlayoutHorizonMaintainResult.ExtendedOneDay;
    }

    public async Task<bool> TryExtendPlayoutHorizonAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var result = await MaintainPlayoutHorizonAsync(channelId, cancellationToken).ConfigureAwait(false);
        return result is PlayoutHorizonMaintainResult.AlreadyAtHorizon
            or PlayoutHorizonMaintainResult.ExtendedOneDay;
    }

    /// <summary>
    /// Extends eligible AI channels that are one day short of the configured playout horizon.
    /// </summary>
    public async Task<int> MaintainEligiblePlayoutHorizonsAsync(CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true)
        {
            return 0;
        }

        var channels = await _db.Channels
            .AsNoTracking()
            .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather)
            .OrderBy(c => c.Number)
            .Select(c => new { c.Id, c.Name, c.FilterJson, c.ContentType })
            .ToListAsync(cancellationToken);

        var extended = 0;
        foreach (var channel in channels)
        {
            if (!IsEligible(new Channel { FilterJson = channel.FilterJson, ContentType = channel.ContentType }))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var maintainResult = await MaintainPlayoutHorizonAsync(channel.Id, cancellationToken)
                .ConfigureAwait(false);
            if (maintainResult != PlayoutHorizonMaintainResult.ExtendedOneDay)
            {
                continue;
            }

            extended++;
            _logger.LogInformation(
                "Extended AI channel {ChannelName} playout by one day (horizon maintenance).",
                channel.Name);
        }

        return extended;
    }

    private async Task<DateTime?> GetLatestPlayoutFinishUtcAsync(
        Guid channelId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        return await _db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Finish > nowUtc)
            .Select(p => (DateTime?)p.Finish)
            .MaxAsync(cancellationToken);
    }

    private async Task<bool> HasDefaultLineupAsync(Guid channelId, CancellationToken cancellationToken)
    {
        return await _db.Lineups
            .AsNoTracking()
            .Where(l => l.ChannelId == channelId && l.IsDefault)
            .AnyAsync(l => l.Slots.Any(s => s.Candidates.Count > 0), cancellationToken);
    }

    private static void SaveGenerateAllState(AiGenerateAllJobState state)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.AiGenerateAllJob = state;
        plugin.SaveConfiguration();
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

        EnqueueAutoApplyChannel(channelId);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                await service.ProcessPendingAutoApplyQueueAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background AI auto-apply queue processing failed.");
            }
        });
    }

    /// <summary>
    /// Processes channels queued for AI auto-apply (lineup + 14-day playout rebuild).
    /// </summary>
    /// <param name="progress">Optional progress reporter for scheduled tasks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of channels processed.</returns>
    public async Task<int> ProcessPendingAutoApplyQueueAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true
            || !Plugin.Instance.Configuration.Ai.AutoApplyOnChannelAdd)
        {
            progress?.Report(100);
            return 0;
        }

        List<Guid> pending;
        lock (PendingQueueLock)
        {
            pending = Plugin.Instance.Configuration.AiPendingAutoApplyChannelIds
                .Distinct()
                .ToList();
        }

        if (pending.Count == 0)
        {
            progress?.Report(100);
            return 0;
        }

        var processed = 0;
        for (var index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channelId = pending[index];
            var result = await ApplyChannelLineupAsync(channelId, cancellationToken: cancellationToken).ConfigureAwait(false);
            RemoveFromPendingQueue(channelId);
            processed++;

            if (result.Ok)
            {
                _logger.LogInformation(
                    "Auto-applied AI lineup for channel {ChannelName} with 14-day playout rebuild.",
                    result.ChannelName);
            }
            else if (!result.WasSkipped)
            {
                _logger.LogWarning(
                    "AI lineup auto-apply failed for channel {ChannelId}: {Error}",
                    channelId,
                    result.Error);
            }

            progress?.Report((index + 1) * 100d / pending.Count);
        }

        return processed;
    }

    private static void EnqueueAutoApplyChannel(Guid channelId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (PendingQueueLock)
        {
            if (!plugin.Configuration.AiPendingAutoApplyChannelIds.Contains(channelId))
            {
                plugin.Configuration.AiPendingAutoApplyChannelIds.Add(channelId);
                plugin.SaveConfiguration();
            }
        }
    }

    private static void RemoveFromPendingQueue(Guid channelId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (PendingQueueLock)
        {
            if (plugin.Configuration.AiPendingAutoApplyChannelIds.Remove(channelId))
            {
                plugin.SaveConfiguration();
            }
        }
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

        QueueApplyToAllEligibleChannelsInBackground("apply-to-all");
    }

    /// <summary>
    /// Queues AI lineup generation for all eligible channels (admin Generate All button).
    /// Returns immediately so reverse proxies do not time out on long LLM/playout runs.
    /// </summary>
    public void QueueManualGenerateAllEligibleChannels()
    {
        if (Plugin.Instance?.Configuration.Ai.Enabled != true || IsGenerateAllJobRunning)
        {
            return;
        }

        QueueStaggeredGenerateAllInBackground("generate-all");
    }

    private void QueueStaggeredGenerateAllInBackground(string operationName)
    {
        _logger.LogInformation("Queueing staggered AI {Operation} for eligible channels.", operationName);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                await service.RunStaggeredGenerateAllAsync(CancellationToken.None).ConfigureAwait(false);
                var job = Plugin.Instance?.Configuration.AiGenerateAllJob;
                if (job is not null)
                {
                    _logger.LogInformation(
                        "Background staggered AI {Operation} finished: {Lineups} lineups, {PlayoutDays} playout days, {FailedLineups} lineup failures, {FailedDays} day failures.",
                        operationName,
                        job.LineupsGenerated,
                        job.PlayoutDaysBuilt,
                        job.LineupsFailed,
                        job.PlayoutDaysFailed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background staggered AI {Operation} failed.", operationName);
                var plugin = Plugin.Instance;
                if (plugin is not null)
                {
                    var job = plugin.Configuration.AiGenerateAllJob;
                    job.IsRunning = false;
                    job.LastError = ex.Message;
                    job.CompletedAt = DateTime.UtcNow;
                    plugin.SaveConfiguration();
                }
            }
        });
    }

    private void QueueApplyToAllEligibleChannelsInBackground(string operationName)
    {
        QueueStaggeredGenerateAllInBackground(operationName);
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
                results.Add(await service.ApplyChannelLineupAsync(channel.Id, cancellationToken: cancellationToken));
            }

            return results;
        }
        finally
        {
            BulkApplyLock.Release();
        }
    }
}

public enum PlayoutHorizonMaintainResult
{
    NeedsFullBuild,
    AlreadyAtHorizon,
    ExtendedOneDay
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
