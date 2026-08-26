using Jellyfin.Plugin.FinTV.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Optionally pre-starts WeatherStar Docker containers when Jellyfin boots.
/// Weather live streams themselves come from ChannelFlow-Server.
/// </summary>
public sealed class DockerAutoStartHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DockerAutoStartHostedService> _logger;

    public DockerAutoStartHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<DockerAutoStartHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = WarmUpAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoStartWeatherStarDocker)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var weatherStarDocker = scope.ServiceProvider.GetRequiredService<WeatherStarDockerService>();

        try
        {
            if (!await weatherStarDocker.IsDockerAvailableAsync(cancellationToken))
            {
                _logger.LogWarning("Auto-start WeatherStar Docker skipped: Docker is not available.");
                return;
            }

            var variant = weatherStarDocker.ResolveLocalVariant(config.WeatherStarBaseUrl)
                ?? WeatherStarDockerVariant.Ws4kp;
            _logger.LogInformation("Auto-starting WeatherStar Docker ({Variant}) on Jellyfin startup.", variant);
            await weatherStarDocker.EnsureRunningAsync(variant, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ChannelFlow could not auto-start WeatherStar Docker.");
        }
    }
}
