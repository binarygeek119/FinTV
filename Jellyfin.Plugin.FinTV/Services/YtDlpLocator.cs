using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Resolves the yt-dlp executable, preferring the bundled binary shipped with the plugin.
/// </summary>
public sealed class YtDlpLocator
{
    private readonly ILogger<YtDlpLocator> _logger;
    private string? _cachedPath;

    public YtDlpLocator(ILogger<YtDlpLocator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the best available yt-dlp or youtube-dl executable path.
    /// </summary>
    public string? Resolve()
    {
        if (!string.IsNullOrEmpty(_cachedPath) && File.Exists(_cachedPath))
        {
            return _cachedPath;
        }

        var bundled = ResolveBundled();
        if (bundled is not null)
        {
            _cachedPath = bundled;
            return bundled;
        }

        _cachedPath = FindOnPath("yt-dlp") ?? FindOnPath("youtube-dl");
        return _cachedPath;
    }

    private string? ResolveBundled()
    {
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(pluginDir))
        {
            return null;
        }

        string? candidate = null;
        if (OperatingSystem.IsWindows())
        {
            candidate = Path.Combine(pluginDir, "tools", "yt-dlp", "win-x64", "yt-dlp.exe");
        }
        else if (OperatingSystem.IsLinux())
        {
            candidate = Path.Combine(pluginDir, "tools", "yt-dlp", "linux-x64", "yt-dlp");
        }

        if (candidate is null || !File.Exists(candidate))
        {
            return null;
        }

        EnsureExecutable(candidate);
        _logger.LogDebug("Using bundled yt-dlp at {Path}", candidate);
        return candidate;
    }

    private void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
            {
                File.SetUnixFileMode(
                    path,
                    mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set execute permission on bundled yt-dlp at {Path}", path);
        }
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim(), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows())
            {
                var exeCandidate = candidate + ".exe";
                if (File.Exists(exeCandidate))
                {
                    return exeCandidate;
                }
            }
        }

        return null;
    }
}
