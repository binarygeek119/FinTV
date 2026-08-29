using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Midnight task that finds black+silence commercial-break spots on videos not yet scanned.
/// </summary>
public sealed class CatalogCommercialBreakScanTask : IScheduledTask
{
    private readonly CatalogCommercialBreakScanService _scan;

    public CatalogCommercialBreakScanTask(CatalogCommercialBreakScanService scan)
    {
        _scan = scan;
    }

    public string Name => "ChannelFlow-Server Commercial Breaks";

    public string Key => "ChannelFlowCommercialBreaks";

    public string Description =>
        "At midnight local time, ffmpeg blackdetect+silencedetect finds commercial-break spots on videos not yet scanned. Off until enabled on the Tasks tab.";

    public string Category => "ChannelFlow-Server";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = 0
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _scan.RunMissingAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
