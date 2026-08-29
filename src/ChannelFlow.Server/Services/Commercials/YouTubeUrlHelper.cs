using System.Text.RegularExpressions;

namespace FinTv.Services;

public static partial class YouTubeUrlHelper
{
    public static bool TryGetVideoId(string? urlOrId, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(urlOrId))
        {
            return false;
        }

        var value = urlOrId.Trim();
        if (VideoIdRegex().IsMatch(value))
        {
            videoId = value;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (VideoIdRegex().IsMatch(id))
            {
                videoId = id;
                return true;
            }
        }

        if (!host.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("youtube-nocookie", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals("v", StringComparison.OrdinalIgnoreCase)
                && VideoIdRegex().IsMatch(Uri.UnescapeDataString(pair[1])))
            {
                videoId = Uri.UnescapeDataString(pair[1]);
                return true;
            }
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if ((segments[i].Equals("embed", StringComparison.OrdinalIgnoreCase)
                    || segments[i].Equals("shorts", StringComparison.OrdinalIgnoreCase)
                    || segments[i].Equals("live", StringComparison.OrdinalIgnoreCase))
                && VideoIdRegex().IsMatch(segments[i + 1]))
            {
                videoId = segments[i + 1];
                return true;
            }
        }

        return false;
    }

    public static string WatchUrl(string? videoId)
        => string.IsNullOrWhiteSpace(videoId)
            ? string.Empty
            : "https://www.youtube.com/watch?v=" + videoId.Trim();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();
}
