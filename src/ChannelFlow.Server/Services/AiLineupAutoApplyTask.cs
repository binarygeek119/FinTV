using FinTv.Configuration;
using FinTv.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

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

    public string Name => "ChannelFlow-Server AI Lineup Auto-Apply (Channel Add)";

    public string Key => "ChannelFlowAiLineupAutoApply";

    public string Description =>
        "Processes queued new-channel AI lineups. The next 14-day guide day is appended at local midnight.";

    public string Category => "ChannelFlow-Server";

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
        var settings = FinTvRuntime.Current?.Configuration.Ai ?? new AiSettings();
        if (!settings.Enabled)
        {
            _logger.LogInformation("ChannelFlow-Server AI lineup auto-apply skipped because AI is disabled.");
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

        progress.Report(100);
        _logger.LogInformation(
            "ChannelFlow-Server AI lineup task finished: {Processed} queued channel(s).",
            processed);
    }
}
