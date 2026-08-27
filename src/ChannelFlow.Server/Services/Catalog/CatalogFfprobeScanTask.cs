using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Midnight task that ffprobes video files still missing chapter metadata.
/// </summary>
public sealed class CatalogFfprobeScanTask : IScheduledTask
{
    private readonly CatalogFfprobeScanService _scan;

    public CatalogFfprobeScanTask(CatalogFfprobeScanService scan)
    {
        _scan = scan;
    }

    public string Name => "ChannelFlow-Server ffprobe Chapters";

    public string Key => "ChannelFlowFfprobeChapters";

    public string Description =>
        "At midnight local time, ffprobe scans video files that still have no chapter data. Files already probed are skipped.";

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
