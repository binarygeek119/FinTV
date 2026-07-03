using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Configures Playwright for Jellyfin plugin hosting on Windows and headless Linux servers.
/// Linux uses a Docker sidecar for Chromium unless PLAYWRIGHT_BROWSERS_PATH is preset (FinTV Jellyfin image).
/// </summary>
public class PlaywrightRuntimeService
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static bool _environmentConfigured;
    private static bool _browsersInstalled;

    private readonly ILogger<PlaywrightRuntimeService> _logger;
    private readonly PlaywrightDockerBrowserService _dockerBrowser;

    public PlaywrightRuntimeService(
        ILogger<PlaywrightRuntimeService> logger,
        PlaywrightDockerBrowserService dockerBrowser)
    {
        _logger = logger;
        _dockerBrowser = dockerBrowser;
    }

    /// <summary>
    /// Gets a value indicating whether weather capture uses Docker-hosted Chromium.
    /// </summary>
    public bool UsesDockerBrowser =>
        OperatingSystem.IsLinux()
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));

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
    /// Connects to a browser using Docker CDP on Linux or a local launch on Windows.
    /// </summary>
    /// <param name="playwright">Playwright instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connected browser.</returns>
    public async Task<IBrowser> ConnectBrowserAsync(IPlaywright playwright, CancellationToken cancellationToken = default)
    {
        if (UsesDockerBrowser)
        {
            await _dockerBrowser.EnsureBrowserReadyAsync(cancellationToken);
            _logger.LogDebug("Connecting to Playwright Docker browser at {CdpEndpoint}", _dockerBrowser.CdpEndpoint);
            return await playwright.Chromium.ConnectOverCDPAsync(_dockerBrowser.CdpEndpoint);
        }

        return await playwright.Chromium.LaunchAsync(CreateLaunchOptions());
    }

    /// <summary>
    /// Releases a browser connection without stopping the shared Docker Chromium process.
    /// </summary>
    /// <param name="browser">Browser to release.</param>
    public async Task ReleaseBrowserAsync(IBrowser browser)
    {
        if (UsesDockerBrowser)
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
        return UsesDockerBrowser
            ? PlaywrightDockerBrowserService.AdjustWeatherPageUrlForDocker(url)
            : url;
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

        if (OperatingSystem.IsLinux() && !UsesDockerBrowser)
        {
            options.Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            ];
        }

        return options;
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

        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", pluginDirectory);

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
                pluginDirectory,
                browsersPath);
        }
        else
        {
            _logger.LogDebug(
                "Playwright configured for Docker browser on Linux. Driver path: {DriverPath}",
                pluginDirectory);
        }

        _environmentConfigured = true;
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
