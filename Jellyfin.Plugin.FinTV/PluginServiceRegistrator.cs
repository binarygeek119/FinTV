using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
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

        ConfigureJsonOptions(serviceCollection);

        serviceCollection.AddDbContext<FinTvDbContext>((sp, options) =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
            Directory.CreateDirectory(plugin.DataFolder);
            Directory.CreateDirectory(plugin.EbsCustomSlatesFolder);
            options.UseSqlite($"Data Source={plugin.DatabasePath}");
        });

        serviceCollection.AddHttpClient(nameof(LlmClientService))
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.All
            });
        serviceCollection.AddHttpClient();
        serviceCollection.AddScoped<ChannelService>();
        serviceCollection.AddScoped<ChannelPresetService>();
        serviceCollection.AddScoped<LineupService>();
        serviceCollection.AddScoped<SpecialPresentationService>();
        serviceCollection.AddScoped<FinTvListService>();
        serviceCollection.AddScoped<SmartSelectionService>();
        serviceCollection.AddScoped<LineupGeneratorService>();
        serviceCollection.AddScoped<CommercialService>();
        serviceCollection.AddScoped<CommercialBrainzClient>();
        serviceCollection.AddScoped<CommercialBrainzFilterService>();
        serviceCollection.AddScoped<CommercialBrainzSyncService>();
        serviceCollection.AddSingleton<YtDlpLocator>();
        serviceCollection.AddScoped<YouTubeCommercialStreamService>();
        serviceCollection.AddScoped<EpgService>();
        serviceCollection.AddScoped<GuideMetadataService>();
        serviceCollection.AddScoped<WeatherGuideMetadataService>();
        serviceCollection.AddScoped<LogoSetService>();
        serviceCollection.AddScoped<HolidayChannelService>();
        serviceCollection.AddScoped<JellyfinCatalogService>();
        serviceCollection.AddScoped<AiCatalogManifestBuilder>();
        serviceCollection.AddScoped<LlmClientService>();
        serviceCollection.AddScoped<AiLineupGeneratorService>();
        serviceCollection.AddScoped<AiChannelAutoApplyService>();
        serviceCollection.AddSingleton<AiChannelGenerateJobService>();
        serviceCollection.AddSingleton<AiLineupAutoApplyTask>();
        serviceCollection.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<AiLineupAutoApplyTask>());
        serviceCollection.AddScoped<EbsService>();
        serviceCollection.AddSingleton<PlaywrightDockerBrowserService>();
        serviceCollection.AddSingleton<WeatherStarDockerService>();
        serviceCollection.AddSingleton<PlaywrightRuntimeService>();
        serviceCollection.AddScoped<WeatherStarChannelService>();
        serviceCollection.AddSingleton<Streaming.JellyfinFfmpegEncodingService>();
        serviceCollection.AddSingleton<StreamService>();
        serviceCollection.AddSingleton<Streaming.FfmpegCommandBuilder>();
        serviceCollection.AddSingleton<PlayoutBuilderService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlayoutBuilderService>());
        serviceCollection.AddHostedService<DatabaseInitializer>();
        serviceCollection.AddHostedService<PluginManifestRepairHostedService>();
        serviceCollection.AddHostedService<DockerAutoStartHostedService>();
        serviceCollection.AddSingleton<BlackframeChapterTask>();
    }

    private static void ConfigureJsonOptions(IServiceCollection serviceCollection)
    {
        serviceCollection.Configure<JsonOptions>(options =>
        {
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.AllowTrailingCommas = true;
        });

        serviceCollection.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.AllowTrailingCommas = true;
        });
    }
}
