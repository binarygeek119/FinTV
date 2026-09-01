using System.Security.Cryptography;
using System.Text;

namespace FinTv.Auth;

/// <summary>
/// Shared secret used by the Jellyfin plugin and generic IPTV URLs.
/// ChannelFlow TV apps receive a unique key from <see cref="FinTv.Services.PairedTvClientStore"/> instead.
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
        => AppendQuery(url, Resolve());

    public static string AppendQuery(string url, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return url + separator + "apiKey=" + Uri.EscapeDataString(apiKey.Trim());
    }

    public static (string M3u, string Epg) BuildLiveTvUrls(string baseUrl)
        => BuildLiveTvUrls(baseUrl, Resolve());

    public static (string M3u, string Epg) BuildLiveTvUrls(string baseUrl, string? apiKey)
    {
        var root = (baseUrl ?? string.Empty).TrimEnd('/');
        return (AppendQuery($"{root}/iptv/channels.m3u", apiKey), AppendQuery($"{root}/iptv/epg.xml", apiKey));
    }

    public static bool Matches(string? provided)
        => KeysEqual(provided, Resolve());

    public static bool KeysEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }
}
