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
            Directory.CreateDirectory(plugin.EbsCustomSlatesFolder);
            options.UseSqlite($"Data Source={plugin.DatabasePath}");
        });

        serviceCollection.AddHttpClient();
        serviceCollection.AddScoped<ChannelService>();
        serviceCollection.AddScoped<ChannelPresetService>();
        serviceCollection.AddScoped<LineupService>();
        serviceCollection.AddScoped<SmartSelectionService>();
        serviceCollection.AddScoped<LineupGeneratorService>();
        serviceCollection.AddScoped<CommercialService>();
        serviceCollection.AddScoped<CommercialBrainzClient>();
        serviceCollection.AddScoped<CommercialBrainzFilterService>();
        serviceCollection.AddScoped<CommercialBrainzSyncService>();
        serviceCollection.AddSingleton<YtDlpLocator>();
        serviceCollection.AddScoped<YouTubeCommercialStreamService>();
        serviceCollection.AddScoped<EpgService>();
        serviceCollection.AddScoped<LogoSetService>();
        serviceCollection.AddScoped<JellyfinCatalogService>();
        serviceCollection.AddScoped<AiCatalogManifestBuilder>();
        serviceCollection.AddScoped<LlmClientService>();
        serviceCollection.AddScoped<AiLineupGeneratorService>();
        serviceCollection.AddScoped<EbsService>();
        serviceCollection.AddSingleton<PlaywrightDockerBrowserService>();
        serviceCollection.AddSingleton<PlaywrightRuntimeService>();
        serviceCollection.AddScoped<WeatherStarChannelService>();
        serviceCollection.AddSingleton<StreamService>();
        serviceCollection.AddSingleton<Streaming.FfmpegCommandBuilder>();
        serviceCollection.AddSingleton<PlayoutBuilderService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlayoutBuilderService>());
        serviceCollection.AddHostedService<DatabaseInitializer>();
        serviceCollection.AddSingleton<BlackframeChapterTask>();
    }
}
