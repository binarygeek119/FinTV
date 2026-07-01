using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.FinTV;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        services.AddDbContext<FinTvDbContext>((sp, options) =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
            Directory.CreateDirectory(plugin.DataFolder);
            options.UseSqlite($"Data Source={plugin.DatabasePath}");
        });

        services.AddHttpClient();
        services.AddScoped<ChannelService>();
        services.AddScoped<LineupService>();
        services.AddScoped<SmartSelectionService>();
        services.AddScoped<LineupGeneratorService>();
        services.AddScoped<CommercialService>();
        services.AddScoped<EpgService>();
        services.AddScoped<LogoSetService>();
        services.AddScoped<JellyfinCatalogService>();
        services.AddScoped<WeatherStarChannelService>();
        services.AddSingleton<StreamService>();
        services.AddSingleton<Streaming.FfmpegCommandBuilder>();
        services.AddHostedService<DatabaseInitializer>();
        services.AddHostedService<PlayoutBuilderService>();
        services.AddSingleton<BlackframeChapterTask>();
    }
}
