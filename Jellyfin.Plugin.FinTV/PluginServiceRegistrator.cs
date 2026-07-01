using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.FinTV;

/// <summary>
/// Registers FinTV services with the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        serviceCollection.AddDbContext<FinTvDbContext>((sp, options) =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
            Directory.CreateDirectory(plugin.DataFolder);
            options.UseSqlite($"Data Source={plugin.DatabasePath}");
        });

        serviceCollection.AddHttpClient();
        serviceCollection.AddScoped<ChannelService>();
        serviceCollection.AddScoped<ChannelPresetService>();
        serviceCollection.AddScoped<LineupService>();
        serviceCollection.AddScoped<SmartSelectionService>();
        serviceCollection.AddScoped<LineupGeneratorService>();
        serviceCollection.AddScoped<CommercialService>();
        serviceCollection.AddScoped<EpgService>();
        serviceCollection.AddScoped<LogoSetService>();
        serviceCollection.AddScoped<JellyfinCatalogService>();
        serviceCollection.AddScoped<EbsService>();
        serviceCollection.AddScoped<WeatherStarChannelService>();
        serviceCollection.AddSingleton<StreamService>();
        serviceCollection.AddSingleton<Streaming.FfmpegCommandBuilder>();
        serviceCollection.AddHostedService<DatabaseInitializer>();
        serviceCollection.AddHostedService<PlayoutBuilderService>();
        serviceCollection.AddSingleton<BlackframeChapterTask>();
    }
}
