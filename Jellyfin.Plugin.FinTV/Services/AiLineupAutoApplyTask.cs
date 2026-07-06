using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Jellyfin scheduled task that processes queued AI lineup auto-apply jobs for new channels.
/// </summary>
public class AiLineupAutoApplyTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiLineupAutoApplyTask> _logger;

    public AiLineupAutoApplyTask(IServiceScopeFactory scopeFactory, ILogger<AiLineupAutoApplyTask> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "FinTV AI Lineup Auto-Apply (Channel Add)";

    public string Key => "FinTVAiLineupAutoApply";

    public string Description =>
        "Processes queued new-channel AI lineups and extends eligible channels by one day when the playout horizon drops to 13 days.";

    public string Category => "FinTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled)
        {
            _logger.LogInformation("FinTV AI lineup auto-apply skipped because AI is disabled.");
            progress.Report(100);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var autoApply = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();

        var processed = 0;
        if (settings.AutoApplyOnChannelAdd)
        {
            processed = await autoApply.ProcessPendingAutoApplyQueueAsync(null, cancellationToken)
                .ConfigureAwait(false);
        }

        var extended = await autoApply.MaintainEligiblePlayoutHorizonsAsync(cancellationToken)
            .ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation(
            "FinTV AI lineup task finished: {Processed} queued channel(s), {Extended} horizon extension(s).",
            processed,
            extended);
    }
}
