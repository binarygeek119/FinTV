using FinTv.Data;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public sealed class MediaServerHealthHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MediaServerHealthHostedService> _logger;

    public MediaServerHealthHostedService(IServiceScopeFactory scopes, ILogger<MediaServerHealthHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
                var ready = scope.ServiceProvider.GetService<DatabaseInitializer>();
                if (ready is { IsReady: false })
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                var servers = scope.ServiceProvider.GetRequiredService<MediaServerService>();
                var ids = await db.MediaServerConnections.AsNoTracking()
                    .Where(c => c.Enabled)
                    .Select(c => c.Id)
                    .ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    try
                    {
                        await servers.TestAsync(id, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Media server health check failed for {Id}", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Media server health pass skipped");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
