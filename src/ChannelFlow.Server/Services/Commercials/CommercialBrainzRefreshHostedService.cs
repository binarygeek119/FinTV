using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Once a night, refreshes CommercialBrainz spots already stored in the Commercials table.
/// </summary>
public sealed class CommercialBrainzRefreshHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommercialBrainzRefreshHostedService> _logger;

    public CommercialBrainzRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<CommercialBrainzRefreshHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = WeatherGuideMetadataService.NextLocalMidnight();
            var delay = next - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromSeconds(5))
            {
                delay = TimeSpan.FromDays(1);
            }

            _logger.LogInformation("Next CommercialBrainz library refresh at {When} (in {Delay})", next, delay);
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
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<CommercialBrainzSyncService>();
                _logger.LogInformation("Refreshing stored CommercialBrainz spots from their saved video links");
                await sync.RefreshStoredVideosAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Midnight CommercialBrainz library refresh failed");
            }
        }
    }
}
