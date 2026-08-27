using FinTv.Data;

namespace FinTv.Services;

/// <summary>
/// Runs the missing-info ffprobe chapter scan at midnight local time.
/// </summary>
public sealed class CatalogFfprobeScanHostedService : BackgroundService
{
    private readonly CatalogFfprobeScanService _scan;
    private readonly CatalogTrueAspectScanService _trueAspect;
    private readonly DatabaseInitializer _database;
    private readonly ILogger<CatalogFfprobeScanHostedService> _logger;

    public CatalogFfprobeScanHostedService(
        CatalogFfprobeScanService scan,
        CatalogTrueAspectScanService trueAspect,
        DatabaseInitializer database,
        ILogger<CatalogFfprobeScanHostedService> logger)
    {
        _scan = scan;
        _trueAspect = trueAspect;
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
            var next = CatalogFfprobeScanService.NextMidnight(DateTimeOffset.Now);
            var delay = next - DateTimeOffset.Now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            _logger.LogInformation("Next midnight chapter and true-aspect scans at {When} (in {Delay})", next, delay);
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
                await _scan.RunMissingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled ffprobe chapter scan failed");
            }

            try
            {
                await _trueAspect.RunMissingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled true-aspect scan failed");
            }
        }
    }
}
