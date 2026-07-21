using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Weekly scheduled task that tags Jellyfin library items with fintv-* channel tags.
/// </summary>
public class FinTvChannelAutoTaggingTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FinTvChannelAutoTaggingTask> _logger;

    public FinTvChannelAutoTaggingTask(IServiceScopeFactory scopeFactory, ILogger<FinTvChannelAutoTaggingTask> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "FinTV AI Channel Auto-Tagging";

    public string Key => "FinTVAiChannelAutoTagging";

    public string Description =>
        "Tags movies, series, and music videos with fintv-* channel tags using built-in channel rules so AI catalog loading is faster.";

    public string Category => "FinTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        if (!settings.AutoTagChannelsWeekly)
        {
            _logger.LogInformation("FinTV channel auto-tagging skipped because weekly auto-tagging is disabled.");
            progress.Report(100);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var tagging = scope.ServiceProvider.GetRequiredService<FinTvChannelTaggingService>();
        await tagging.RunAsync(fullRetag: false, progress, cancellationToken).ConfigureAwait(false);
    }
}
