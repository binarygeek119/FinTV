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

    public PlaywrightDockerBrowserService(ILogger<PlaywrightDockerBrowserService> logger)
    {
        _logger = logger;
    }

    public string CdpEndpoint => $"http://127.0.0.1:{DefaultCdpPort}";

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
            if (await IsContainerRunningAsync(cancellationToken))
            {
                await WaitForCdpReadyAsync(cancellationToken);
                return;
            }

            await RemoveStaleContainerAsync(cancellationToken);
            await StartContainerAsync(cancellationToken);
            await WaitForCdpReadyAsync(cancellationToken);
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

    private async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting Playwright Docker browser container {ContainerName} from {Image}",
            ContainerName,
            DefaultImage);

        var launchCommand = new StringBuilder()
            .Append("CHROME=$(find /ms-playwright -name chrome -path '*/chrome-linux/*' | head -n 1); ")
            .Append("test -n \"$CHROME\" || exit 1; ")
            .Append("exec \"$CHROME\" ")
            .Append("--headless=new ")
            .Append("--remote-debugging-port=9222 ")
            .Append("--remote-debugging-address=0.0.0.0 ")
            .Append("--no-sandbox ")
            .Append("--disable-setuid-sandbox ")
            .Append("--disable-dev-shm-usage ")
            .Append("--disable-gpu")
            .ToString();

        var result = await Cli.Wrap("docker")
            .WithArguments([
                "run",
                "-d",
                "--name", ContainerName,
                "--shm-size=1gb",
                "--add-host=host.docker.internal:host-gateway",
                "-p", $"{DefaultCdpPort}:9222",
                "--restart", "unless-stopped",
                DefaultImage,
                "/bin/sh",
                "-c",
                launchCommand
            ])
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

    private async Task WaitForCdpReadyAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await http.GetAsync($"{CdpEndpoint}/json/version", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug(ex, "Waiting for Playwright Docker CDP on {CdpEndpoint}", CdpEndpoint);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Playwright Docker CDP did not become ready at {CdpEndpoint}. Check container logs with: docker logs {ContainerName}");
    }
}
