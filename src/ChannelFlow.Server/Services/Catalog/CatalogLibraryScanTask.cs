using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Six-hour task that syncs every enabled library connection for new files.
/// </summary>
public sealed class CatalogLibraryScanTask : IScheduledTask
{
    private readonly CatalogLibraryScanService _scan;

    public CatalogLibraryScanTask(CatalogLibraryScanService scan)
    {
        _scan = scan;
    }

    public string Name => "ChannelFlow-Server Library Scan";

    public string Key => "ChannelFlowLibraryScan";

    public string Description =>
        "Syncs each enabled library connection every 6 hours (00:00, 06:00, 12:00, 18:00 local) so new files appear in the catalog.";

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
        await _scan.RunAllAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
