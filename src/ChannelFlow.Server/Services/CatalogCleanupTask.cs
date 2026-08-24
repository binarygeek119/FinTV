using FinTv.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Daily task that marks Jellyfin-removed catalog rows missing, then deletes them after the grace period.
/// </summary>
public sealed class CatalogCleanupTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CatalogCleanupTask> _logger;

    public CatalogCleanupTask(IServiceScopeFactory scopeFactory, ILogger<CatalogCleanupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "ChannelFlow-Server Catalog Cleanup";

    public string Key => "ChannelFlowCatalogCleanup";

    public string Description =>
        "Marks catalog items missing when Jellyfin no longer reports them or their remapped local file is gone, then deletes them after the configured grace period.";

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
        using var scope = _scopeFactory.CreateScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<CatalogCleanupService>();
        var result = await cleanup.RunAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation(
            "Catalog cleanup finished: marked {Marked}, removed {Removed}, still missing {Missing}.",
            result.MarkedMissing,
            result.Removed,
            result.CurrentlyMissing);
    }
}
