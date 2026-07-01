using Jellyfin.Plugin.FinTV.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Data;

public class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IServiceScopeFactory scopeFactory, ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        await SchemaMigrator.MigrateAsync(db, _logger, cancellationToken);

        if (!await db.CommercialPresets.AnyAsync(cancellationToken))
        {
            db.CommercialPresets.Add(new Domain.CommercialPreset
            {
                Name = "Default",
                BreakMode = Domain.CommercialBreakMode.ChaptersThenTimer,
                TimerIntervalMinutes = 12,
                PostRollCount = 2
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("FinTV database initialized");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
