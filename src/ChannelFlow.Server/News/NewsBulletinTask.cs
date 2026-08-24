using FinTv.Domain;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsBulletinTask : IScheduledTask
{
    private readonly NewsBulletinService _bulletins;
    private readonly ILogger<NewsBulletinTask> _logger;

    public NewsBulletinTask(NewsBulletinService bulletins, ILogger<NewsBulletinTask> logger)
    {
        _bulletins = bulletins;
        _logger = logger;
    }

    public string Name => "FlowWire News Video";

    public string Key => "ChannelFlowNewsBulletin";

    public string Description =>
        "Encodes a news bulletin MP4 every 6 hours (00:00, 06:00, 12:00, 18:00 local). Skips when there are no new RSS stories, or fewer than the configured minimum.";

    public string Category => "ChannelFlow-Server";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        foreach (var hour in new[] { 0, 6, 12, 18 })
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(hour).Ticks
            };
        }
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await _bulletins.RunAsync(scheduled: true, cancellationToken);
        progress.Report(100);
        if (result.Created)
        {
            _logger.LogInformation("News video task created {Path}", result.Path);
            return;
        }

        _logger.LogInformation("News video task skipped: {Reason}", result.SkipReason);
    }
}
