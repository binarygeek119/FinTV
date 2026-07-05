using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Resolves the yt-dlp executable from FINTV_YTDLP_PATH or the process PATH.
/// The FinTV Jellyfin Docker image installs yt-dlp at /usr/local/bin/yt-dlp.
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

        var overridePath = Environment.GetEnvironmentVariable("FINTV_YTDLP_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            _cachedPath = overridePath;
            _logger.LogDebug("Using yt-dlp from FINTV_YTDLP_PATH at {Path}", overridePath);
            return overridePath;
        }

        _cachedPath = FindOnPath("yt-dlp") ?? FindOnPath("youtube-dl");
        if (_cachedPath is not null)
        {
            _logger.LogDebug("Using yt-dlp from PATH at {Path}", _cachedPath);
        }

        return _cachedPath;
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
