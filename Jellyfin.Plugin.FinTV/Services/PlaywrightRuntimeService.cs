using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Configures Playwright for Jellyfin plugin hosting on Windows and headless Linux servers.
/// Linux uses the fintv-playwright-chromium Docker sidecar over CDP (Windows launches local Chromium).
/// </summary>
public class PlaywrightRuntimeService
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static bool _environmentConfigured;
    private static bool _browsersInstalled;

    private readonly ILogger<PlaywrightRuntimeService> _logger;
    private readonly PlaywrightDockerBrowserService _dockerBrowser;
    private readonly WeatherStarDockerService _weatherDocker;

    public PlaywrightRuntimeService(
        ILogger<PlaywrightRuntimeService> logger,
        PlaywrightDockerBrowserService dockerBrowser,
        WeatherStarDockerService weatherDocker)
    {
        _logger = logger;
        _dockerBrowser = dockerBrowser;
        _weatherDocker = weatherDocker;
    }

    /// <summary>
    /// Gets a value indicating whether weather capture uses the Docker CDP sidecar instead of a local browser.
    /// </summary>
    public bool UsesDockerBrowser =>
        OperatingSystem.IsLinux()
        && (PlaywrightDockerBrowserService.RunsInsideDocker()
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")));

    /// <summary>
    /// Creates a Playwright instance after ensuring drivers and Chromium are available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Playwright instance.</returns>
    public async Task<IPlaywright> CreateAsync(CancellationToken cancellationToken = default)
    {
        ConfigureEnvironment();
        if (!UsesDockerBrowser)
        {
            await EnsureChromiumInstalledAsync(cancellationToken);
        }

        return await Playwright.CreateAsync();
    }

    /// <summary>
    /// Connects to a browser using the Docker CDP sidecar on Linux or a local launch on Windows.
    /// </summary>
    /// <param name="playwright">Playwright instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connected browser and whether it uses the shared Docker CDP sidecar.</returns>
    public async Task<(IBrowser Browser, bool SharedDockerCdp)> ConnectBrowserAsync(
        IPlaywright playwright,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            if (await _dockerBrowser.IsDockerAvailableAsync(cancellationToken))
            {
                await _dockerBrowser.EnsureBrowserReadyAsync(cancellationToken);
                _logger.LogDebug("Connecting to Playwright Docker browser at {CdpEndpoint}", _dockerBrowser.CdpEndpoint);
                var dockerBrowser = await playwright.Chromium.ConnectOverCDPAsync(_dockerBrowser.CdpEndpoint);
                return (dockerBrowser, true);
            }

            if (PlaywrightDockerBrowserService.RunsInsideDocker())
            {
                throw new InvalidOperationException(
                    "Docker is required for FinTV weather channels when Jellyfin runs in a container. Mount "
                    + "/var/run/docker.sock into Jellyfin and ensure the Jellyfin user can run docker.");
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")))
            {
                var localBrowser = await playwright.Chromium.LaunchAsync(CreateLaunchOptions());
                return (localBrowser, false);
            }

            throw new InvalidOperationException(
                "Docker is required for FinTV weather channels on Linux. Mount /var/run/docker.sock into Jellyfin "
                + "and ensure the Jellyfin user can run docker.");
        }

        var browser = await playwright.Chromium.LaunchAsync(CreateLaunchOptions());
        return (browser, false);
    }

    /// <summary>
    /// Releases a browser connection without stopping the shared Docker Chromium process.
    /// </summary>
    /// <param name="browser">Browser to release.</param>
    /// <param name="sharedDockerCdp">Whether the browser came from the shared Docker CDP sidecar.</param>
    public async Task ReleaseBrowserAsync(IBrowser browser, bool sharedDockerCdp = false)
    {
        if (UsesDockerBrowser || sharedDockerCdp)
        {
            foreach (var context in browser.Contexts.ToArray())
            {
                await context.CloseAsync();
            }

            return;
        }

        await browser.CloseAsync();
    }

    /// <summary>
    /// Rewrites localhost WeatherStar URLs so a Docker-hosted browser can reach the Jellyfin host.
    /// </summary>
    /// <param name="url">Weather page URL.</param>
    /// <returns>Adjusted URL when Docker mode is active.</returns>
    public string AdjustWeatherPageUrlForRuntime(string url)
    {
        if (_dockerBrowser.SharesJellyfinNetwork && _weatherDocker.SharesJellyfinNetwork)
        {
            return url;
        }

        if (ShouldRewriteLocalhostForDocker(url))
        {
            return PlaywrightDockerBrowserService.AdjustWeatherPageUrlForDocker(url);
        }

        return url;
    }

    /// <summary>
    /// Builds browser launch options suitable for Jellyfin service hosts.
    /// </summary>
    /// <returns>Headless launch options.</returns>
    public BrowserTypeLaunchOptions CreateLaunchOptions()
    {
        var options = new BrowserTypeLaunchOptions
        {
            Headless = true
        };

        if (OperatingSystem.IsLinux())
        {
            options.Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            ];

            var chromeExecutable = ResolveBundledChromeExecutable();
            if (chromeExecutable is not null)
            {
                options.ExecutablePath = chromeExecutable;
                _logger.LogDebug("Using bundled Playwright Chrome executable at {ExecutablePath}", chromeExecutable);
            }
        }

        return options;
    }

    private static bool ShouldRewriteLocalhostForDocker(string url)
    {
        if (UsesDockerBrowserFlag())
        {
            return true;
        }

        if (!File.Exists("/.dockerenv"))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesDockerBrowserFlag()
        => OperatingSystem.IsLinux()
            && (PlaywrightDockerBrowserService.RunsInsideDocker()
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")));

    private static string? ResolveBundledChromeExecutable()
    {
        var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(browsersPath) || !Directory.Exists(browsersPath))
        {
            return null;
        }

        foreach (var chromiumDir in Directory.EnumerateDirectories(browsersPath, "chromium-*"))
        {
            var chrome = Path.Combine(chromiumDir, "chrome-linux", "chrome");
            if (File.Exists(chrome))
            {
                return chrome;
            }
        }

        return null;
    }

    private void ConfigureEnvironment()
    {
        if (_environmentConfigured)
        {
            return;
        }

        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("FinTV plugin is not initialized.");

        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? plugin.DataFolder;

        var driverDirectory = ResolvePlaywrightDriverDirectory(pluginDirectory);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", driverDirectory);
        if (!string.Equals(driverDirectory, pluginDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Using Playwright driver at {DriverPath} (plugin directory: {PluginDirectory})",
                driverDirectory,
                pluginDirectory);
        }

        if (!UsesDockerBrowser)
        {
            var presetBrowsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            var browsersPath = string.IsNullOrWhiteSpace(presetBrowsersPath)
                ? Path.Combine(plugin.DataFolder, "playwright-browsers")
                : presetBrowsersPath;

            if (string.IsNullOrWhiteSpace(presetBrowsersPath))
            {
                Directory.CreateDirectory(browsersPath);
            }

            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
            _logger.LogDebug(
                "Playwright configured for local browser. Driver path: {DriverPath}. Browser path: {BrowserPath}",
                driverDirectory,
                browsersPath);
        }
        else
        {
            _logger.LogDebug(
                "Playwright configured for Docker browser on Linux. Driver path: {DriverPath}",
                driverDirectory);
        }

        _environmentConfigured = true;
    }

    private static string ResolvePlaywrightDriverDirectory(string pluginDirectory)
    {
        var candidates = new List<string>();
        var presetDriverPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH");
        if (!string.IsNullOrWhiteSpace(presetDriverPath))
        {
            candidates.Add(presetDriverPath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            candidates.Add(pluginDirectory);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (HasPlaywrightDriver(candidate))
            {
                return candidate;
            }
        }

        return pluginDirectory;
    }

    private static bool HasPlaywrightDriver(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var playwrightDirectory = Path.Combine(directory, ".playwright");
        if (!Directory.Exists(playwrightDirectory))
        {
            return false;
        }

        var nodeRoot = Path.Combine(playwrightDirectory, "node");
        if (Directory.Exists(nodeRoot))
        {
            foreach (var platformDirectory in Directory.EnumerateDirectories(nodeRoot))
            {
                var nodeExecutable = OperatingSystem.IsWindows()
                    ? Path.Combine(platformDirectory, "node.exe")
                    : Path.Combine(platformDirectory, "node");
                if (File.Exists(nodeExecutable))
                {
                    return true;
                }
            }
        }

        return File.Exists(Path.Combine(playwrightDirectory, "package", "cli.js"));
    }

    private async Task EnsureChromiumInstalledAsync(CancellationToken cancellationToken)
    {
        if (_browsersInstalled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")))
        {
            _browsersInstalled = true;
            _logger.LogDebug("Using preset Playwright browsers from PLAYWRIGHT_BROWSERS_PATH");
            return;
        }

        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            if (_browsersInstalled)
            {
                return;
            }

            _logger.LogInformation("Ensuring Playwright Chromium is installed for FinTV weather channels");
            var exitCode = Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Playwright Chromium install failed with exit code {exitCode}.");
            }

            _browsersInstalled = true;
            _logger.LogInformation("Playwright Chromium is ready for FinTV weather channels");
        }
        finally
        {
            InstallLock.Release();
        }
    }
}
