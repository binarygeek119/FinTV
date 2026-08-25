using FinTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// At local midnight, appends one playout day for AI channels that already have a 14-day guide.
/// </summary>
public sealed class AiPlayoutHorizonHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiPlayoutHorizonHostedService> _logger;

    public AiPlayoutHorizonHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AiPlayoutHorizonHostedService> logger)
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

            _logger.LogInformation("Next AI playout day-15 append at {When} (in {Delay})", next, delay);
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
                if (FinTvRuntime.Current?.Configuration.Ai.Enabled != true)
                {
                    _logger.LogInformation("Midnight AI playout append skipped because AI is disabled");
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var autoApply = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                _logger.LogInformation("Appending the next playout day for AI channels as the current day ends");
                var extended = await autoApply.MaintainEligiblePlayoutHorizonsAsync(stoppingToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Midnight AI playout append finished: {Extended} channel(s)", extended);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Midnight AI playout day append failed");
            }
        }
    }
}
