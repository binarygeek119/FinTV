using System.Security.Cryptography;

namespace FinTv.Auth;

/// <summary>
/// Shared secret used by the Jellyfin plugin and IPTV URLs.
/// Created at startup when missing and managed from the General page.
/// </summary>
public static class PluginApiKey
{
    public static string? Resolve()
    {
        var fromConfig = FinTvRuntime.Current?.Configuration.ApiKey;
        return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig.Trim();
    }

    public static string Generate()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public static string AppendQuery(string url)
    {
        var key = Resolve();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return url + separator + "apiKey=" + Uri.EscapeDataString(key);
    }

    public static (string M3u, string Epg) BuildLiveTvUrls(string baseUrl)
    {
        var root = (baseUrl ?? string.Empty).TrimEnd('/');
        return (AppendQuery($"{root}/iptv/channels.m3u"), AppendQuery($"{root}/iptv/epg.xml"));
    }

    public static bool Matches(string? provided)
    {
        var expected = Resolve();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(provided, expected, StringComparison.Ordinal);
    }
}
