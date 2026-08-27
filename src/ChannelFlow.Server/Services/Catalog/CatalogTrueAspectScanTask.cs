using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Midnight task that measures active picture inside the raster for videos still missing TrueAspectRatio.
/// </summary>
public sealed class CatalogTrueAspectScanTask : IScheduledTask
{
    private readonly CatalogTrueAspectScanService _scan;

    public CatalogTrueAspectScanTask(CatalogTrueAspectScanService scan)
    {
        _scan = scan;
    }

    public string Name => "ChannelFlow-Server True Aspect";

    public string Key => "ChannelFlowTrueAspect";

    public string Description =>
        "At midnight local time, ffmpeg cropdetect samples five points in each video that still has no TrueAspectRatio. Files already measured are skipped.";

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
