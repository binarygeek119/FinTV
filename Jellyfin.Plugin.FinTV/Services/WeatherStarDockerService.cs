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
    private DockerSidecarNetworkResolution _networkResolution = new();

    public WeatherStarDockerService(ILogger<WeatherStarDockerService> logger)
    {
        _logger = logger;
    }

    public bool SharesJellyfinNetwork => _networkResolution.SharesJellyfinNetwork;

    public async Task<WeatherStarDockerStatus> GetStatusAsync(
        WeatherStarDockerVariant variant,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(variant);
        var settings = GetSettings(variant);
        var dockerAvailable = await IsDockerAvailableAsync(cancellationToken);
        var running = dockerAvailable && await IsContainerRunningAsync(definition.ContainerName, cancellationToken);
        _networkResolution = await DockerSidecarNetworkHelper.ResolveSidecarNetworkAsync(_logger, cancellationToken);

        var jellyfinContainerRef = _networkResolution.JellyfinContainerRef
            ?? await DockerSidecarNetworkHelper.ResolveJellyfinContainerRefAsync(cancellationToken);
        var sidecarNetworkParent = running
            ? await DockerSidecarNetworkHelper.GetSidecarNetworkParentRefAsync(definition.ContainerName, cancellationToken)
            : null;
        var sharesJellyfinNetwork = running
            ? !string.IsNullOrWhiteSpace(sidecarNetworkParent)
            : _networkResolution.SharesJellyfinNetwork;
        var staleNetworkAttachment = running
            && await DockerSidecarNetworkHelper.IsStaleNetworkAttachmentAsync(
                jellyfinContainerRef,
                sidecarNetworkParent,
                cancellationToken);

        var httpReachable = false;
        var httpListeningInsideSidecar = false;
        if (running && !staleNetworkAttachment)
        {
            httpListeningInsideSidecar = await TryContainerInternalProbeAsync(
                definition.ContainerName,
                definition.ContainerPort,
                cancellationToken);
            httpReachable = await ProbeHttpReadyAsync(definition, settings, sharesJellyfinNetwork, cancellationToken);
        }

        var baseUrl = BuildBaseUrl(definition, settings, sharesJellyfinNetwork);

        return new WeatherStarDockerStatus
        {
            Variant = variant,
            DockerAvailable = dockerAvailable,
            Running = running,
            HttpReachable = httpReachable,
            HttpListeningInsideSidecar = httpListeningInsideSidecar,
            StaleNetworkAttachment = staleNetworkAttachment,
            JellyfinContainerRef = jellyfinContainerRef,
            SidecarNetworkParent = sidecarNetworkParent,
            SharesJellyfinNetwork = sharesJellyfinNetwork,
            JellyfinInDocker = DockerSidecarNetworkHelper.RunsInsideDocker(),
            ContainerName = definition.ContainerName,
            Image = settings.Image,
            HostPort = settings.HostPort,
            BaseUrl = baseUrl,
            ConfiguredBaseUrl = Plugin.Instance?.Configuration.WeatherStarBaseUrl
                ?? WeatherStarChannelService.DefaultWeatherStarBaseUrl,
            StatusMessage = BuildStatusMessage(
                dockerAvailable,
                running,
                httpReachable,
                httpListeningInsideSidecar,
                staleNetworkAttachment,
                sharesJellyfinNetwork,
                baseUrl,
                definition.ContainerName)
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

            _networkResolution = await DockerSidecarNetworkHelper.ResolveSidecarNetworkAsync(_logger, cancellationToken);
            var settings = GetSettings(variant);
            var sharesJellyfinNetwork = _networkResolution.SharesJellyfinNetwork;

            if (await IsContainerRunningAsync(definition.ContainerName, cancellationToken))
            {
                var sidecarNetworkParent = await DockerSidecarNetworkHelper.GetSidecarNetworkParentRefAsync(
                    definition.ContainerName,
                    cancellationToken);
                var jellyfinContainerRef = _networkResolution.JellyfinContainerRef
                    ?? await DockerSidecarNetworkHelper.ResolveJellyfinContainerRefAsync(cancellationToken);

                if (await DockerSidecarNetworkHelper.IsStaleNetworkAttachmentAsync(
                        jellyfinContainerRef,
                        sidecarNetworkParent,
                        cancellationToken))
                {
                    _logger.LogWarning(
                        "WeatherStar {Variant} container {ContainerName} is attached to stale network parent {NetworkParent}; expected {JellyfinContainer}. Recreating it.",
                        variant,
                        definition.ContainerName,
                        sidecarNetworkParent ?? "(unknown)",
                        jellyfinContainerRef ?? "(unknown)");
                    await RemoveStaleContainerAsync(definition.ContainerName, cancellationToken);
                }
                else if (await WaitForHttpReadyAsync(definition, settings, sharesJellyfinNetwork, cancellationToken, throwOnTimeout: false))
                {
                    return await GetStatusAsync(variant, cancellationToken);
                }
                else
                {
                    _logger.LogWarning(
                        "WeatherStar {Variant} container {ContainerName} is running but HTTP is not reachable from Jellyfin; recreating it",
                        variant,
                        definition.ContainerName);
                    await RemoveStaleContainerAsync(definition.ContainerName, cancellationToken);
                }
            }

            await RemoveStaleContainerAsync(definition.ContainerName, cancellationToken);
            await StartContainerAsync(variant, _networkResolution.Network, cancellationToken);
            await WaitForHttpReadyAsync(definition, settings, sharesJellyfinNetwork, cancellationToken, throwOnTimeout: true);

            _logger.LogInformation(
                "{Variant} Docker container is ready at {BaseUrl} using image {Image}{NetworkSuffix}",
                variant,
                BuildBaseUrl(definition, settings, sharesJellyfinNetwork),
                settings.Image,
                sharesJellyfinNetwork
                    ? " (sharing Jellyfin network namespace)"
                    : $" on host port {settings.HostPort}");

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
        var effectivePort = ResolveEffectivePort(definition, settings, UsesSharedJellyfinLoopback());
        var configuredPort = uri.IsDefaultPort ? effectivePort : uri.Port;
        return configuredPort == effectivePort;
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

    private static bool UsesSharedJellyfinLoopback()
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FINTV_DOCKER_NETWORK"))
            && DockerSidecarNetworkHelper.RunsInsideDocker();

    private static int ResolveEffectivePort(
        WeatherStarDockerDefinition definition,
        WeatherStarDockerSettings settings,
        bool sharesJellyfinNetwork)
        => sharesJellyfinNetwork ? definition.ContainerPort : settings.HostPort;

    private static string BuildBaseUrl(
        WeatherStarDockerDefinition definition,
        WeatherStarDockerSettings settings,
        bool sharesJellyfinNetwork)
        => BuildBaseUrl(ResolveEffectivePort(definition, settings, sharesJellyfinNetwork));

    private static bool IsLocalHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
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

    private async Task StartContainerAsync(
        WeatherStarDockerVariant variant,
        string? network,
        CancellationToken cancellationToken)
    {
        var definition = GetDefinition(variant);
        var settings = GetSettings(variant);
        var sharesJellyfinNetwork = !string.IsNullOrWhiteSpace(network)
            && network.StartsWith("container:", StringComparison.OrdinalIgnoreCase);

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

        var networkSuffix = sharesJellyfinNetwork
            ? " sharing Jellyfin network namespace"
            : $" on host port {settings.HostPort}";
        _logger.LogInformation(
            "Starting {Variant} Docker container {ContainerName} from {Image}{NetworkSuffix}",
            variant,
            definition.ContainerName,
            settings.Image,
            networkSuffix);

        var runArgs = new List<string>
        {
            "run",
            "-d",
            "--name", definition.ContainerName,
            "--restart", "unless-stopped"
        };

        if (!string.IsNullOrWhiteSpace(network))
        {
            runArgs.Add("--network");
            runArgs.Add(network);
            if (!network.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            {
                runArgs.Add("--add-host=host.docker.internal:host-gateway");
            }
        }
        else
        {
            runArgs.Add("-p");
            runArgs.Add($"{settings.HostPort}:{definition.ContainerPort}");
            runArgs.Add("--add-host=host.docker.internal:host-gateway");
        }

        runArgs.Add(settings.Image);

        var result = await Cli.Wrap("docker")
            .WithArguments(runArgs)
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

    private async Task<bool> WaitForHttpReadyAsync(
        WeatherStarDockerDefinition definition,
        WeatherStarDockerSettings settings,
        bool sharesJellyfinNetwork,
        CancellationToken cancellationToken,
        bool throwOnTimeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var urls = BuildHealthCheckUrls(definition, settings, sharesJellyfinNetwork).ToArray();
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var url in urls)
            {
                if (await TryHttpProbeAsync(http, url, cancellationToken))
                {
                    _logger.LogDebug("WeatherStar HTTP ready at {Url}", url);
                    return true;
                }
            }

            if (await TryContainerInternalProbeAsync(definition.ContainerName, definition.ContainerPort, cancellationToken))
            {
                _logger.LogDebug(
                    "WeatherStar ready via in-container probe on {ContainerName}",
                    definition.ContainerName);
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        if (!throwOnTimeout)
        {
            return false;
        }

        var primaryUrl = BuildBaseUrl(definition, settings, sharesJellyfinNetwork);
        throw new TimeoutException(
            $"WeatherStar container did not become ready at {primaryUrl}. "
            + $"When Jellyfin runs in Docker, FinTV shares Jellyfin's network namespace so WeatherStar is on loopback "
            + $"(e.g. http://127.0.0.1:{definition.ContainerPort}). "
            + $"Verify with `docker logs {definition.ContainerName}`. "
            + "Set FINTV_JELLYFIN_CONTAINER if auto-detect fails (e.g. Jellyfin on Unraid). "
            + $"Recreate the container after replacing Jellyfin (`docker rm -f {definition.ContainerName}`).");
    }

    private async Task<bool> ProbeHttpReadyAsync(
        WeatherStarDockerDefinition definition,
        WeatherStarDockerSettings settings,
        bool sharesJellyfinNetwork,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var url in BuildHealthCheckUrls(definition, settings, sharesJellyfinNetwork))
        {
            if (await TryHttpProbeAsync(http, url, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> BuildHealthCheckUrls(
        WeatherStarDockerDefinition definition,
        WeatherStarDockerSettings settings,
        bool sharesJellyfinNetwork)
    {
        yield return BuildBaseUrl(definition, settings, sharesJellyfinNetwork);

        if (!sharesJellyfinNetwork && File.Exists("/.dockerenv"))
        {
            yield return $"http://host.docker.internal:{settings.HostPort}";
        }
    }

    private async Task<bool> TryHttpProbeAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Waiting for HTTP on {Url}", url);
            return false;
        }
    }

    private async Task<bool> TryContainerInternalProbeAsync(
        string containerName,
        int containerPort,
        CancellationToken cancellationToken)
    {
        var url = $"http://127.0.0.1:{containerPort}/";

        var wget = await Cli.Wrap("docker")
            .WithArguments(["exec", containerName, "wget", "-q", "-O", "/dev/null", url])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (wget.ExitCode == 0)
        {
            return true;
        }

        var curl = await Cli.Wrap("docker")
            .WithArguments(["exec", containerName, "curl", "-sf", url])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return curl.ExitCode == 0;
    }

    private static string? BuildStatusMessage(
        bool dockerAvailable,
        bool running,
        bool httpReachable,
        bool httpListeningInsideSidecar,
        bool staleNetworkAttachment,
        bool sharesJellyfinNetwork,
        string baseUrl,
        string containerName)
    {
        if (!dockerAvailable)
        {
            return "Docker is not available from Jellyfin. Mount /var/run/docker.sock and ensure the Jellyfin user can run docker.";
        }

        if (!running)
        {
            return "Stopped. Click Start.";
        }

        if (httpReachable)
        {
            return sharesJellyfinNetwork
                ? $"Running · HTTP reachable at {baseUrl} (sharing Jellyfin network namespace)"
                : $"Running · HTTP reachable at {baseUrl}";
        }

        if (staleNetworkAttachment)
        {
            return "Container is attached to an old Jellyfin container network. Click Stop, then Start. "
                + "If this persists after a Jellyfin container recreate, set FINTV_JELLYFIN_CONTAINER=Jellyfin on the Jellyfin template.";
        }

        if (httpListeningInsideSidecar)
        {
            return "WeatherStar responds inside the container but Jellyfin cannot reach it on loopback. "
                + "This usually means a stale network namespace — click Stop, then Start.";
        }

        return $"Container is running but HTTP is not responding. Click Stop, then Start. Check docker logs {containerName} if it keeps failing.";
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
