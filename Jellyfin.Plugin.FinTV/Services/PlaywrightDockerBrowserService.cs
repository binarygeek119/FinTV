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

    public static bool RunsInsideDocker() => File.Exists("/.dockerenv");

    public async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["version", "--format", "{{.Server.Version}}"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    public async Task EnsureBrowserReadyAsync(CancellationToken cancellationToken = default)
    {
        await ContainerLock.WaitAsync(cancellationToken);
        try
        {
            var sidecarNetwork = await ResolveSidecarNetworkAsync(cancellationToken);

            if (await IsContainerRunningAsync(cancellationToken))
            {
                if (await TryWaitForCdpReadyAsync(cancellationToken))
                {
                    return;
                }

                _logger.LogWarning(
                    "Playwright sidecar {ContainerName} is running but CDP is not reachable from Jellyfin; recreating it",
                    ContainerName);
                await RemoveStaleContainerAsync(cancellationToken);
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
        var overrideNetwork = Environment.GetEnvironmentVariable("FINTV_DOCKER_NETWORK");
        if (!string.IsNullOrWhiteSpace(overrideNetwork))
        {
            _jellyfinSharesSidecarLoopback = false;
            return overrideNetwork.Trim();
        }

        if (!RunsInsideDocker())
        {
            _jellyfinSharesSidecarLoopback = false;
            return null;
        }

        var containerRef = await ResolveJellyfinContainerRefAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(containerRef))
        {
            _jellyfinSharesSidecarLoopback = false;
            return null;
        }

        // Chrome CDP accepts loopback only; sharing Jellyfin's network namespace avoids broken
        // Docker port forwards (connection reset) on bridge/host/Unraid setups.
        _jellyfinSharesSidecarLoopback = true;
        _logger.LogInformation(
            "Jellyfin container {ContainerRef} will share network namespace with Playwright sidecar; CDP at http://127.0.0.1:{Port}",
            containerRef,
            DefaultCdpPort);
        return $"container:{containerRef}";
    }

    private static async Task<string?> ResolveJellyfinContainerRefAsync(CancellationToken cancellationToken)
    {
        var containerRef = Environment.GetEnvironmentVariable("FINTV_JELLYFIN_CONTAINER");
        if (!string.IsNullOrWhiteSpace(containerRef))
        {
            return containerRef.Trim();
        }

        if (!File.Exists("/etc/hostname"))
        {
            return null;
        }

        var hostname = (await File.ReadAllTextAsync("/etc/hostname", cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var nameResult = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.Name}}", hostname])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (nameResult.ExitCode == 0)
        {
            var name = nameResult.StandardOutput.Trim().TrimStart('/');
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return hostname;
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

    private async Task<bool> TryWaitForCdpReadyAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

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
}
