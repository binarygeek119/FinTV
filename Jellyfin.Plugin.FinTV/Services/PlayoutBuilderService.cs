using Jellyfin.Plugin.FinTV.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class PlayoutBuilderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlayoutBuilderService> _logger;

    public PlayoutBuilderService(IServiceScopeFactory scopeFactory, ILogger<PlayoutBuilderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BuildAllChannelsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playout builder failed");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    public async Task BuildAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();
        var commercialService = scope.ServiceProvider.GetRequiredService<CommercialService>();

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await commercialService.SyncCommercialLibraryAsync(cancellationToken);

        var days = Plugin.Instance?.Configuration.PlayoutDaysToBuild ?? 3;
        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(days);

        var channels = await db.Channels.Where(c => c.Enabled).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            await generator.BuildPlayoutAsync(channel, start, end, cancellationToken);
            _logger.LogInformation("Built playout for channel {Channel}", channel.Name);
        }
    }
}
