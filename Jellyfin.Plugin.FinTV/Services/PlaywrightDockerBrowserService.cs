using System.Text;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Runs Chromium from Playwright's official Docker image on Linux and exposes CDP locally.
/// </summary>
public class PlaywrightDockerBrowserService
{
    public const string ContainerName = "fintv-playwright-chromium";
    public const string DefaultImage = "mcr.microsoft.com/playwright:v1.49.0-jammy";
    public const int DefaultCdpPort = 9222;

    private static readonly SemaphoreSlim ContainerLock = new(1, 1);

    private readonly ILogger<PlaywrightDockerBrowserService> _logger;
    private string _activeCdpEndpoint = BuildDefaultCdpEndpoint(DefaultCdpPort);
    private bool _jellyfinSharesSidecarLoopback;

    public PlaywrightDockerBrowserService(ILogger<PlaywrightDockerBrowserService> logger)
    {
        _logger = logger;
    }

    public string CdpEndpoint => _activeCdpEndpoint;

    public static string BuildDefaultCdpEndpoint(int port = DefaultCdpPort)
    {
        return $"http://127.0.0.1:{port}";
    }

    public static bool RunsInsideDocker() => DockerSidecarNetworkHelper.RunsInsideDocker();

    public bool SharesJellyfinNetwork => _jellyfinSharesSidecarLoopback;

    public async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["version", "--format", "{{.Server.Version}}"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    public async Task<PlaywrightDockerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var dockerAvailable = await IsDockerAvailableAsync(cancellationToken);
        var running = dockerAvailable && await IsContainerRunningAsync(cancellationToken);
        var jellyfinContainerRef = await DockerSidecarNetworkHelper.ResolveJellyfinContainerRefAsync(cancellationToken);
        var sidecarNetworkParent = running
            ? await DockerSidecarNetworkHelper.GetSidecarNetworkParentRefAsync(ContainerName, cancellationToken)
            : null;
        var staleNetworkAttachment = running
            && await DockerSidecarNetworkHelper.IsStaleNetworkAttachmentAsync(
                jellyfinContainerRef,
                sidecarNetworkParent,
                cancellationToken);
        var cdpReachable = false;
        var chromeListeningInsideSidecar = false;

        if (running)
        {
            await ResolveSidecarNetworkAsync(cancellationToken);
            chromeListeningInsideSidecar = await IsChromeRespondingInsideSidecarAsync(cancellationToken);
            if (!staleNetworkAttachment)
            {
                cdpReachable = await ProbeCdpReadyAsync(cancellationToken);
            }
        }

        return new PlaywrightDockerStatus
        {
            DockerAvailable = dockerAvailable,
            Running = running,
            CdpReachable = cdpReachable,
            ChromeListeningInsideSidecar = chromeListeningInsideSidecar,
            StaleNetworkAttachment = staleNetworkAttachment,
            JellyfinContainerRef = jellyfinContainerRef,
            SidecarNetworkParent = sidecarNetworkParent,
            ContainerName = ContainerName,
            Image = DefaultImage,
            CdpPort = DefaultCdpPort,
            CdpEndpoint = CdpEndpoint,
            JellyfinInDocker = RunsInsideDocker(),
            SharesJellyfinNetwork = _jellyfinSharesSidecarLoopback,
            StatusMessage = BuildStatusMessage(
                dockerAvailable,
                running,
                cdpReachable,
                chromeListeningInsideSidecar,
                staleNetworkAttachment,
                jellyfinContainerRef,
                sidecarNetworkParent)
        };
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await ContainerLock.WaitAsync(cancellationToken);
        try
        {
            await RemoveStaleContainerAsync(cancellationToken);
        }
        finally
        {
            ContainerLock.Release();
        }
    }

    public async Task EnsureBrowserReadyAsync(CancellationToken cancellationToken = default)
    {
        await ContainerLock.WaitAsync(cancellationToken);
        try
        {
            var sidecarNetwork = await ResolveSidecarNetworkAsync(cancellationToken);

            if (await IsContainerRunningAsync(cancellationToken))
            {
                var sidecarNetworkParent = await DockerSidecarNetworkHelper.GetSidecarNetworkParentRefAsync(
                    ContainerName,
                    cancellationToken);
                var jellyfinContainerRef = await DockerSidecarNetworkHelper.ResolveJellyfinContainerRefAsync(cancellationToken);
                if (await DockerSidecarNetworkHelper.IsStaleNetworkAttachmentAsync(
                        jellyfinContainerRef,
                        sidecarNetworkParent,
                        cancellationToken))
                {
                    _logger.LogWarning(
                        "Playwright sidecar {ContainerName} is attached to stale network parent {NetworkParent}; expected {JellyfinContainer}. Recreating it.",
                        ContainerName,
                        sidecarNetworkParent ?? "(unknown)",
                        jellyfinContainerRef ?? "(unknown)");
                    await RemoveStaleContainerAsync(cancellationToken);
                }
                else if (await TryWaitForCdpReadyAsync(cancellationToken))
                {
                    return;
                }
                else
                {
                    _logger.LogWarning(
                        "Playwright sidecar {ContainerName} is running but CDP is not reachable from Jellyfin; recreating it",
                        ContainerName);
                    await RemoveStaleContainerAsync(cancellationToken);
                }
            }

            await StartContainerAsync(sidecarNetwork, cancellationToken);

            if (!await TryWaitForCdpReadyAsync(cancellationToken))
            {
                throw new TimeoutException(BuildCdpTimeoutMessage());
            }

            _logger.LogInformation(
                "Playwright Docker browser is ready at {CdpEndpoint} using image {Image}",
                CdpEndpoint,
                DefaultImage);
        }
        finally
        {
            ContainerLock.Release();
        }
    }

    public static string AdjustWeatherPageUrlForDocker(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (!IsLocalHost(uri.Host))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Host = "host.docker.internal"
        };

        return builder.Uri.ToString();
    }

    private static bool IsLocalHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsContainerRunningAsync(CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.State.Running}}", ContainerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return result.ExitCode == 0
            && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RemoveStaleContainerAsync(CancellationToken cancellationToken)
    {
        var inspect = await Cli.Wrap("docker")
            .WithArguments(["inspect", ContainerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (inspect.ExitCode != 0)
        {
            return;
        }

        _logger.LogInformation("Removing stale Playwright Docker container {ContainerName}", ContainerName);
        await Cli.Wrap("docker")
            .WithArguments(["rm", "-f", ContainerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

    private async Task StartContainerAsync(string? network, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Pulling Playwright Docker image {Image}",
            DefaultImage);

        var pull = await Cli.Wrap("docker")
            .WithArguments(["pull", DefaultImage])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (pull.ExitCode != 0)
        {
            _logger.LogWarning(
                "Docker pull failed for {Image}: {Details}",
                DefaultImage,
                string.IsNullOrWhiteSpace(pull.StandardError) ? pull.StandardOutput : pull.StandardError);
        }

        var networkSuffix = _jellyfinSharesSidecarLoopback
            ? " sharing Jellyfin network namespace (CDP on 127.0.0.1)"
            : string.IsNullOrWhiteSpace(network) ? " with host-published CDP port" : $" on network {network}";
        _logger.LogInformation(
            "Starting Playwright Docker browser container {ContainerName} from {Image}{NetworkSuffix}",
            ContainerName,
            DefaultImage,
            networkSuffix);

        var launchCommand = BuildChromeLaunchCommand(_jellyfinSharesSidecarLoopback);

        var runArgs = new List<string>
        {
            "run",
            "-d",
            "--name", ContainerName,
            "--shm-size=1gb"
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
            runArgs.Add($"{DefaultCdpPort}:9222");
            runArgs.Add("--add-host=host.docker.internal:host-gateway");
        }

        runArgs.Add(DefaultImage);
        runArgs.Add("/bin/sh");
        runArgs.Add("-c");
        runArgs.Add(launchCommand);

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
                $"Failed to start Playwright Docker container. Ensure Docker is installed and the Jellyfin user can run docker. {details}".Trim());
        }
    }

    private static string BuildChromeLaunchCommand(bool sharedLoopback)
    {
        var command = new StringBuilder()
            .Append("CHROME=$(find /ms-playwright -name chrome -path '*/chrome-linux/*' | head -n 1); ")
            .Append("test -n \"$CHROME\" || exit 1; ")
            .Append("exec \"$CHROME\" ")
            .Append("--headless=new ")
            .Append("--remote-debugging-port=")
            .Append(DefaultCdpPort)
            .Append(' ');

        if (!sharedLoopback)
        {
            command.Append("--remote-debugging-address=0.0.0.0 ");
        }

        return command
            .Append("--remote-allow-origins=* ")
            .Append("--no-sandbox ")
            .Append("--disable-setuid-sandbox ")
            .Append("--disable-dev-shm-usage ")
            .Append("--disable-gpu")
            .ToString();
    }

    private async Task<string?> ResolveSidecarNetworkAsync(CancellationToken cancellationToken)
    {
        var resolution = await DockerSidecarNetworkHelper.ResolveSidecarNetworkAsync(_logger, cancellationToken);
        _jellyfinSharesSidecarLoopback = resolution.SharesJellyfinNetwork;

        if (resolution.SharesJellyfinNetwork)
        {
            _logger.LogInformation(
                "Playwright sidecar CDP will be reachable at http://127.0.0.1:{Port}",
                DefaultCdpPort);
        }

        return resolution.Network;
    }

    private static IEnumerable<string> BuildCdpProbeUrls(bool jellyfinSharesSidecarLoopback)
    {
        if (!RunsInsideDocker())
        {
            yield return BuildDefaultCdpEndpoint(DefaultCdpPort);
            yield break;
        }

        if (jellyfinSharesSidecarLoopback)
        {
            yield return BuildDefaultCdpEndpoint(DefaultCdpPort);
            yield break;
        }

        yield return $"http://{ContainerName}:{DefaultCdpPort}";
        yield return $"http://host.docker.internal:{DefaultCdpPort}";
        yield return BuildDefaultCdpEndpoint(DefaultCdpPort);
    }

    private string BuildCdpTimeoutMessage()
    {
        return
            $"Playwright Docker CDP did not become reachable from Jellyfin at {string.Join(", ", BuildCdpProbeUrls(_jellyfinSharesSidecarLoopback))}. "
            + $"Chrome may still be running inside the sidecar — check `docker logs {ContainerName}`. "
            + "When Jellyfin runs in Docker, FinTV shares Jellyfin's network namespace so CDP on "
            + $"http://127.0.0.1:{DefaultCdpPort} is reachable (required because Chrome rejects Docker port forwards). "
            + "Set FINTV_JELLYFIN_CONTAINER if auto-detect fails (e.g. Jellyfin on Unraid). "
            + $"Recreate the sidecar after replacing the Jellyfin container (`docker rm -f {ContainerName}`).";
    }

    private async Task<bool> ProbeCdpReadyAsync(CancellationToken cancellationToken)
    {
        using var http = CreateCdpHttpClient();

        foreach (var url in BuildCdpProbeUrls(_jellyfinSharesSidecarLoopback))
        {
            if (await TryHttpProbeAsync(http, $"{url}/json/version", cancellationToken))
            {
                _activeCdpEndpoint = url;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryWaitForCdpReadyAsync(CancellationToken cancellationToken)
    {
        using var http = CreateCdpHttpClient();

        var probeUrls = BuildCdpProbeUrls(_jellyfinSharesSidecarLoopback).ToArray();
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var url in probeUrls)
            {
                if (await TryHttpProbeAsync(http, $"{url}/json/version", cancellationToken))
                {
                    _activeCdpEndpoint = url;
                    _logger.LogDebug("Playwright Docker CDP reachable from Jellyfin at {Url}", url);
                    return true;
                }
            }

            if (await IsChromeRespondingInsideSidecarAsync(cancellationToken))
            {
                _logger.LogDebug(
                    "Playwright Chrome is listening inside {ContainerName}; waiting for Jellyfin network route",
                    ContainerName);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return false;
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
            _logger.LogDebug(ex, "Waiting for Playwright Docker CDP on {Url}", url);
            return false;
        }
    }

    private async Task<bool> IsChromeRespondingInsideSidecarAsync(CancellationToken cancellationToken)
    {
        var url = $"http://127.0.0.1:{DefaultCdpPort}/json/version";

        var curl = await Cli.Wrap("docker")
            .WithArguments(["exec", ContainerName, "curl", "-sf", url])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (curl.ExitCode == 0)
        {
            return true;
        }

        var wget = await Cli.Wrap("docker")
            .WithArguments(["exec", ContainerName, "wget", "-q", "-O", "/dev/null", url])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return wget.ExitCode == 0;
    }

    private static HttpClient CreateCdpHttpClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = null,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    private static string? BuildStatusMessage(
        bool dockerAvailable,
        bool running,
        bool cdpReachable,
        bool chromeListeningInsideSidecar,
        bool staleNetworkAttachment,
        string? jellyfinContainerRef,
        string? sidecarNetworkParent)
    {
        if (!dockerAvailable)
        {
            return "Docker is not available from Jellyfin. Mount /var/run/docker.sock and ensure the Jellyfin user can run docker.";
        }

        if (!running)
        {
            return "Sidecar is stopped. Click Start sidecar.";
        }

        if (cdpReachable)
        {
            return "CDP is reachable from Jellyfin.";
        }

        if (staleNetworkAttachment)
        {
            return "Sidecar is attached to an old Jellyfin container network. Click Stop, then Start. "
                + "If this persists after a Jellyfin container recreate, set FINTV_JELLYFIN_CONTAINER=Jellyfin on the Jellyfin template.";
        }

        if (chromeListeningInsideSidecar)
        {
            return "Chrome is running inside the sidecar but Jellyfin cannot reach CDP on 127.0.0.1:9222. "
                + "This usually means the sidecar is on a stale network namespace — click Stop, then Start.";
        }

        return "Sidecar container is running but Chromium CDP is not responding. Click Stop, then Start. "
            + $"Check docker logs {ContainerName} if it keeps failing.";
    }
}

/// <summary>
/// Runtime status for the Playwright Chromium Docker CDP sidecar.
/// </summary>
public class PlaywrightDockerStatus
{
    public bool DockerAvailable { get; set; }

    public bool Running { get; set; }

    public bool CdpReachable { get; set; }

    public bool ChromeListeningInsideSidecar { get; set; }

    public bool StaleNetworkAttachment { get; set; }

    public string? JellyfinContainerRef { get; set; }

    public string? SidecarNetworkParent { get; set; }

    public string? StatusMessage { get; set; }

    public string ContainerName { get; set; } = PlaywrightDockerBrowserService.ContainerName;

    public string Image { get; set; } = PlaywrightDockerBrowserService.DefaultImage;

    public int CdpPort { get; set; } = PlaywrightDockerBrowserService.DefaultCdpPort;

    public string CdpEndpoint { get; set; } = PlaywrightDockerBrowserService.BuildDefaultCdpEndpoint();

    public bool JellyfinInDocker { get; set; }

    public bool SharesJellyfinNetwork { get; set; }
}
