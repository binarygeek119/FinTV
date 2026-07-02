using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Configures Playwright for Jellyfin plugin hosting on Windows and headless Linux servers.
/// </summary>
public class PlaywrightRuntimeService
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static bool _environmentConfigured;
    private static bool _browsersInstalled;

    private readonly ILogger<PlaywrightRuntimeService> _logger;

    public PlaywrightRuntimeService(ILogger<PlaywrightRuntimeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a Playwright instance after ensuring drivers and Chromium are available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Playwright instance.</returns>
    public async Task<IPlaywright> CreateAsync(CancellationToken cancellationToken = default)
    {
        ConfigureEnvironment();
        await EnsureChromiumInstalledAsync(cancellationToken);
        return await Playwright.CreateAsync();
    }

    /// <summary>
    /// Builds browser launch options suitable for Jellyfin service hosts.
    /// </summary>
    /// <returns>Headless launch options.</returns>
    public BrowserTypeLaunchOptions CreateLaunchOptions()
    {
        var args = new List<string>();
        if (OperatingSystem.IsLinux())
        {
            args.Add("--no-sandbox");
            args.Add("--disable-setuid-sandbox");
            args.Add("--disable-dev-shm-usage");
            args.Add("--disable-gpu");
        }

        return new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = args
        };
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

        var browsersPath = Path.Combine(plugin.DataFolder, "playwright-browsers");
        Directory.CreateDirectory(browsersPath);

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", pluginDirectory);

        _environmentConfigured = true;
        _logger.LogDebug(
            "Playwright configured. Driver search path: {DriverPath}. Browser path: {BrowserPath}",
            pluginDirectory,
            browsersPath);
    }

    private async Task EnsureChromiumInstalledAsync(CancellationToken cancellationToken)
    {
        if (_browsersInstalled)
        {
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
                var hint = OperatingSystem.IsLinux()
                    ? " On Linux headless servers, also install OS dependencies (for example: sudo npx playwright install-deps chromium)."
                    : string.Empty;
                throw new InvalidOperationException($"Playwright Chromium install failed with exit code {exitCode}.{hint}");
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
