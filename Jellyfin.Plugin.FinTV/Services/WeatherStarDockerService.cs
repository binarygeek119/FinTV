using CliWrap;
using CliWrap.Buffered;
using Jellyfin.Plugin.FinTV.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Runs self-hosted WeatherStar Docker containers (ws4kp and ws3kp).
/// </summary>
public class WeatherStarDockerService
{
    private static readonly Dictionary<WeatherStarDockerVariant, SemaphoreSlim> Locks = new()
    {
        [WeatherStarDockerVariant.Ws4kp] = new SemaphoreSlim(1, 1),
        [WeatherStarDockerVariant.Ws3kp] = new SemaphoreSlim(1, 1)
    };

    private readonly ILogger<WeatherStarDockerService> _logger;

    public WeatherStarDockerService(ILogger<WeatherStarDockerService> logger)
    {
        _logger = logger;
    }

    public async Task<WeatherStarDockerStatus> GetStatusAsync(
        WeatherStarDockerVariant variant,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(variant);
        var settings = GetSettings(variant);
        var dockerAvailable = await IsDockerAvailableAsync(cancellationToken);
        var running = dockerAvailable && await IsContainerRunningAsync(definition.ContainerName, cancellationToken);

        return new WeatherStarDockerStatus
        {
            Variant = variant,
            DockerAvailable = dockerAvailable,
            Running = running,
            ContainerName = definition.ContainerName,
            Image = settings.Image,
            HostPort = settings.HostPort,
            BaseUrl = BuildBaseUrl(settings.HostPort),
            ConfiguredBaseUrl = Plugin.Instance?.Configuration.WeatherStarBaseUrl
                ?? WeatherStarChannelService.DefaultWeatherStarBaseUrl
        };
    }

    public async Task<WeatherStarDockerCombinedStatus> GetCombinedStatusAsync(CancellationToken cancellationToken = default)
    {
        var ws4kp = await GetStatusAsync(WeatherStarDockerVariant.Ws4kp, cancellationToken);
        var ws3kp = await GetStatusAsync(WeatherStarDockerVariant.Ws3kp, cancellationToken);
        var configured = ws4kp.ConfiguredBaseUrl;

        return new WeatherStarDockerCombinedStatus
        {
            Ws4kp = ws4kp,
            Ws3kp = ws3kp,
            ConfiguredBaseUrl = configured,
            UsingLocalWs4kp = IsLocalUrl(configured, WeatherStarDockerVariant.Ws4kp),
            UsingLocalWs3kp = IsLocalUrl(configured, WeatherStarDockerVariant.Ws3kp)
        };
    }

    public async Task<WeatherStarDockerStatus> EnsureRunningAsync(
        WeatherStarDockerVariant variant,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(variant);
        var gate = Locks[variant];
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsDockerAvailableAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Docker is not available. Install Docker and ensure the Jellyfin user can run docker.");
            }

            if (await IsContainerRunningAsync(definition.ContainerName, cancellationToken))
            {
                await WaitForHttpReadyAsync(GetSettings(variant).HostPort, definition.ContainerName, cancellationToken);
                return await GetStatusAsync(variant, cancellationToken);
            }

            await RemoveStaleContainerAsync(definition.ContainerName, cancellationToken);
            await StartContainerAsync(variant, cancellationToken);
            await WaitForHttpReadyAsync(GetSettings(variant).HostPort, definition.ContainerName, cancellationToken);

            var settings = GetSettings(variant);
            _logger.LogInformation(
                "{Variant} Docker container is ready at {BaseUrl} using image {Image}",
                variant,
                BuildBaseUrl(settings.HostPort),
                settings.Image);

            return await GetStatusAsync(variant, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WeatherStarDockerStatus> StopAsync(
        WeatherStarDockerVariant variant,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(variant);
        var gate = Locks[variant];
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsDockerAvailableAsync(cancellationToken))
            {
                return await GetStatusAsync(variant, cancellationToken);
            }

            var inspect = await Cli.Wrap("docker")
                .WithArguments(["inspect", definition.ContainerName])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (inspect.ExitCode == 0)
            {
                _logger.LogInformation("Stopping {Variant} Docker container {ContainerName}", variant, definition.ContainerName);
                await Cli.Wrap("docker")
                    .WithArguments(["rm", "-f", definition.ContainerName])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);
            }

            return await GetStatusAsync(variant, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool IsLocalUrl(string? baseUrl, WeatherStarDockerVariant variant)
    {
        if (!Uri.TryCreate(WeatherStarChannelService.NormalizeWeatherStarBaseUrl(baseUrl), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsLocalHost(uri.Host))
        {
            return false;
        }

        var definition = GetDefinition(variant);
        var settings = GetSettings(variant);
        var configuredPort = uri.IsDefaultPort ? definition.DefaultHostPort : uri.Port;
        return configuredPort == settings.HostPort;
    }

    public WeatherStarDockerVariant? ResolveLocalVariant(string? baseUrl)
    {
        if (IsLocalUrl(baseUrl, WeatherStarDockerVariant.Ws4kp))
        {
            return WeatherStarDockerVariant.Ws4kp;
        }

        if (IsLocalUrl(baseUrl, WeatherStarDockerVariant.Ws3kp))
        {
            return WeatherStarDockerVariant.Ws3kp;
        }

        return null;
    }

    public static string BuildBaseUrl(int hostPort) => $"http://127.0.0.1:{hostPort}";

    public void UpdateSettings(WeatherStarDockerVariant variant, int? hostPort, string? image)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
        IWeatherStarDockerSettings settings = variant switch
        {
            WeatherStarDockerVariant.Ws4kp => plugin.Configuration.Ws4kp,
            WeatherStarDockerVariant.Ws3kp => plugin.Configuration.Ws3kp,
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        if (hostPort is int port)
        {
            settings.HostPort = Math.Clamp(port, 1, 65535);
        }

        if (!string.IsNullOrWhiteSpace(image))
        {
            settings.Image = image.Trim();
        }
    }

    private static WeatherStarDockerDefinition GetDefinition(WeatherStarDockerVariant variant)
        => variant switch
        {
            WeatherStarDockerVariant.Ws4kp => WeatherStarDockerDefinition.Ws4kp,
            WeatherStarDockerVariant.Ws3kp => WeatherStarDockerDefinition.Ws3kp,
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

    private static WeatherStarDockerSettings GetSettings(WeatherStarDockerVariant variant)
    {
        var plugin = Plugin.Instance;
        var definition = GetDefinition(variant);
        IWeatherStarDockerSettings settings = variant switch
        {
            WeatherStarDockerVariant.Ws4kp => plugin?.Configuration.Ws4kp ?? new Ws4kpDockerSettings(),
            WeatherStarDockerVariant.Ws3kp => plugin?.Configuration.Ws3kp ?? new Ws3kpDockerSettings(),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        return new WeatherStarDockerSettings
        {
            HostPort = Math.Clamp(settings.HostPort, 1, 65535),
            Image = string.IsNullOrWhiteSpace(settings.Image) ? definition.DefaultImage : settings.Image.Trim()
        };
    }

    private static bool IsLocalHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["version", "--format", "{{.Server.Version}}"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private async Task<bool> IsContainerRunningAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.State.Running}}", containerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return result.ExitCode == 0
            && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RemoveStaleContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var inspect = await Cli.Wrap("docker")
            .WithArguments(["inspect", containerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (inspect.ExitCode != 0)
        {
            return;
        }

        _logger.LogInformation("Removing stale Docker container {ContainerName}", containerName);
        await Cli.Wrap("docker")
            .WithArguments(["rm", "-f", containerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

    private async Task StartContainerAsync(WeatherStarDockerVariant variant, CancellationToken cancellationToken)
    {
        var definition = GetDefinition(variant);
        var settings = GetSettings(variant);

        _logger.LogInformation(
            "Pulling {Variant} Docker image {Image}",
            variant,
            settings.Image);

        var pull = await Cli.Wrap("docker")
            .WithArguments(["pull", settings.Image])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (pull.ExitCode != 0)
        {
            _logger.LogWarning(
                "Docker pull failed for {Image}: {Details}",
                settings.Image,
                string.IsNullOrWhiteSpace(pull.StandardError) ? pull.StandardOutput : pull.StandardError);
        }

        _logger.LogInformation(
            "Starting {Variant} Docker container {ContainerName} from {Image} on host port {HostPort}",
            variant,
            definition.ContainerName,
            settings.Image,
            settings.HostPort);

        var result = await Cli.Wrap("docker")
            .WithArguments([
                "run",
                "-d",
                "--name", definition.ContainerName,
                "-p", $"{settings.HostPort}:{definition.ContainerPort}",
                "--restart", "unless-stopped",
                settings.Image
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(
                $"Failed to start {variant} Docker container. Ensure Docker is installed and the Jellyfin user can run docker. {details}".Trim());
        }
    }

    private async Task WaitForHttpReadyAsync(int hostPort, string containerName, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var url = BuildBaseUrl(hostPort);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await http.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug(ex, "Waiting for HTTP on {Url}", url);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"WeatherStar container did not become ready at {url}. Check container logs with: docker logs {containerName}");
    }
}

public enum WeatherStarDockerVariant
{
    Ws4kp = 0,
    Ws3kp = 1
}

public sealed class WeatherStarDockerDefinition
{
    public static readonly WeatherStarDockerDefinition Ws4kp = new(
        WeatherStarDockerVariant.Ws4kp,
        "fintv-ws4kp",
        "ghcr.io/netbymatt/ws4kp",
        8080,
        8080);

    public static readonly WeatherStarDockerDefinition Ws3kp = new(
        WeatherStarDockerVariant.Ws3kp,
        "fintv-ws3kp",
        "ghcr.io/netbymatt/ws3kp",
        8083,
        8083);

    private WeatherStarDockerDefinition(
        WeatherStarDockerVariant variant,
        string containerName,
        string defaultImage,
        int containerPort,
        int defaultHostPort)
    {
        Variant = variant;
        ContainerName = containerName;
        DefaultImage = defaultImage;
        ContainerPort = containerPort;
        DefaultHostPort = defaultHostPort;
    }

    public WeatherStarDockerVariant Variant { get; }

    public string ContainerName { get; }

    public string DefaultImage { get; }

    public int ContainerPort { get; }

    public int DefaultHostPort { get; }
}

public class WeatherStarDockerSettings
{
    public int HostPort { get; set; }

    public string Image { get; set; } = string.Empty;
}

public class WeatherStarDockerStatus
{
    public WeatherStarDockerVariant Variant { get; set; }

    public bool DockerAvailable { get; set; }

    public bool Running { get; set; }

    public string ContainerName { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public int HostPort { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string ConfiguredBaseUrl { get; set; } = WeatherStarChannelService.DefaultWeatherStarBaseUrl;
}

public class WeatherStarDockerCombinedStatus
{
    public WeatherStarDockerStatus Ws4kp { get; set; } = new();

    public WeatherStarDockerStatus Ws3kp { get; set; } = new();

    public string ConfiguredBaseUrl { get; set; } = WeatherStarChannelService.DefaultWeatherStarBaseUrl;

    public bool UsingLocalWs4kp { get; set; }

    public bool UsingLocalWs3kp { get; set; }
}
