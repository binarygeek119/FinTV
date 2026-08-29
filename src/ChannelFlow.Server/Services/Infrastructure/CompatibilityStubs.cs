using FinTv.Configuration;

namespace FinTv.Services;

/// <summary>
/// Starts the Tasks-tab commercial-break scan (legacy commercials API).
/// </summary>
public sealed class BlackframeChapterTask
{
    private readonly CatalogCommercialBreakScanService _scan;

    public BlackframeChapterTask(CatalogCommercialBreakScanService scan)
    {
        _scan = scan;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_scan.TryBegin())
        {
            progress.Report(100);
            return;
        }

        await _scan.RunMissingAsync(begin: false, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}

public enum WeatherStarDockerVariant
{
    Ws4kp = 0,
    Ws3kp = 1
}

public class WeatherStarDockerStatus
{
    public bool DockerAvailable { get; set; }

    public bool Running { get; set; }

    public bool HttpReachable { get; set; }

    public bool HttpListeningInsideSidecar { get; set; }

    public bool StaleNetworkAttachment { get; set; }

    public string? JellyfinContainerRef { get; set; }

    public string? SidecarNetworkParent { get; set; }

    public bool SharesJellyfinNetwork { get; set; }

    public bool JellyfinInDocker { get; set; }

    public string? StatusMessage { get; set; }

    public string ContainerName { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public int HostPort { get; set; }

    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";
}

public class WeatherStarDockerCombinedStatus
{
    public WeatherStarDockerStatus Ws4kp { get; set; } = new();

    public WeatherStarDockerStatus Ws3kp { get; set; } = new();

    public string? ConfiguredBaseUrl { get; set; }

    public bool UsingLocalWs4kp { get; set; }

    public bool UsingLocalWs3kp { get; set; }
}

public sealed class WeatherStarDockerService
{
    public Task<WeatherStarDockerCombinedStatus> GetCombinedStatusAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(new WeatherStarDockerCombinedStatus
        {
            Ws4kp = Status(WeatherStarDockerVariant.Ws4kp),
            Ws3kp = Status(WeatherStarDockerVariant.Ws3kp),
            ConfiguredBaseUrl = FinTvRuntime.Current?.Configuration.WeatherStarBaseUrl,
            UsingLocalWs4kp = true,
            UsingLocalWs3kp = true
        });
    }

    public Task<WeatherStarDockerStatus> GetStatusAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(Status(variant));
    }

    public void UpdateSettings(WeatherStarDockerVariant variant, int? hostPort, string? image)
    {
        var config = FinTvRuntime.Current?.Configuration;
        if (config is null)
        {
            return;
        }

        if (variant == WeatherStarDockerVariant.Ws4kp)
        {
            if (hostPort.HasValue)
            {
                config.Ws4kp.HostPort = hostPort.Value;
            }

            if (!string.IsNullOrWhiteSpace(image))
            {
                config.Ws4kp.Image = image;
            }
        }
        else
        {
            if (hostPort.HasValue)
            {
                config.Ws3kp.HostPort = hostPort.Value;
            }

            if (!string.IsNullOrWhiteSpace(image))
            {
                config.Ws3kp.Image = image;
            }
        }

        FinTvRuntime.Current?.SaveConfiguration();
    }

    public Task EnsureRunningAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        _ = variant;
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task StopAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        _ = variant;
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private static WeatherStarDockerStatus Status(WeatherStarDockerVariant variant)
    {
        var port = variant == WeatherStarDockerVariant.Ws4kp
            ? (FinTvRuntime.Current?.Configuration.Ws4kp.HostPort ?? 8080)
            : (FinTvRuntime.Current?.Configuration.Ws3kp.HostPort ?? 8083);
        return new WeatherStarDockerStatus
        {
            DockerAvailable = false,
            Running = true,
            HttpReachable = true,
            HttpListeningInsideSidecar = true,
            StatusMessage = "Native WeatherStar compositor.",
            ContainerName = variant == WeatherStarDockerVariant.Ws4kp ? "ws4kp" : "ws3kp",
            Image = "native",
            HostPort = port,
            BaseUrl = $"http://127.0.0.1:{port}"
        };
    }
}
