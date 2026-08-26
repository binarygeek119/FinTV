using FinTv.Data;
using FinTv.News;

namespace FinTv.Services;

/// <summary>
/// Runs the library catalog scan at 00:00, 06:00, 12:00, and 18:00 local time.
/// </summary>
public sealed class CatalogLibraryScanHostedService : BackgroundService
{
    private readonly CatalogLibraryScanService _scan;
    private readonly DatabaseInitializer _database;
    private readonly ILogger<CatalogLibraryScanHostedService> _logger;

    public CatalogLibraryScanHostedService(
        CatalogLibraryScanService scan,
        DatabaseInitializer database,
        ILogger<CatalogLibraryScanHostedService> logger)
    {
        _scan = scan;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _database.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NewsBulletinService.NextSixHourMark(DateTimeOffset.Now);
            var delay = next - DateTimeOffset.Now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            _logger.LogInformation("Next library catalog scan at {When} (in {Delay})", next, delay);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _scan.RunAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled library catalog scan failed");
            }
        }
    }
}
