using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public sealed class WeatherGuideRefreshHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeatherGuideRefreshHostedService> _logger;

    public WeatherGuideRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<WeatherGuideRefreshHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        TryQueue(force: false, "startup");

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = WeatherGuideMetadataService.NextLocalMidnight();
            var delay = next - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromSeconds(5))
            {
                delay = TimeSpan.FromDays(1);
            }

            _logger.LogInformation("Next weather guide refresh at {When} (in {Delay})", next, delay);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            TryQueue(force: true, "midnight");
        }
    }

    private void TryQueue(bool force, string reason)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var weatherGuide = scope.ServiceProvider.GetRequiredService<WeatherGuideMetadataService>();
            if (weatherGuide.IsGenerating)
            {
                _logger.LogInformation("Weather guide refresh skipped ({Reason}): generation already running", reason);
                return;
            }

            _logger.LogInformation("Starting {Reason} weather guide refresh from the Weather tab source", reason);
            weatherGuide.QueueGenerateCache(force);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Weather guide refresh could not start ({Reason})", reason);
        }
    }
}
