using FinTv.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public sealed class ScheduledTaskHost : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledTaskHost> _logger;

    public ScheduledTaskHost(IServiceScopeFactory scopeFactory, ILogger<ScheduledTaskHost> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(24) - DateTime.UtcNow.TimeOfDay + TimeSpan.FromHours(4);
            if (delay < TimeSpan.FromMinutes(5))
            {
                delay += TimeSpan.FromHours(24);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
                using var scope = _scopeFactory.CreateScope();
                var task = scope.ServiceProvider.GetRequiredService<AiLineupAutoApplyTask>();
                await task.ExecuteAsync(new Progress<double>(), stoppingToken);
                var cleanup = scope.ServiceProvider.GetRequiredService<CatalogCleanupTask>();
                await cleanup.ExecuteAsync(new Progress<double>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled ChannelFlow task failed");
            }
        }
    }
}
