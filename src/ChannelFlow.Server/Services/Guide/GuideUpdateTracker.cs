using System.Net;
using System.Net.Sockets;
using FinTv;
using FinTv.Auth;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Tracks when channel playout (XMLTV guide data) last changed and tells the Jellyfin plugin to reload listings.
/// </summary>
public sealed class GuideUpdateTracker
{
    public const string PluginRefreshPath = "/ChannelFlow/api/bridge/server-refresh-guide";

    private static readonly TimeSpan NotifyDebounce = TimeSpan.FromSeconds(300);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<GuideUpdateTracker> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _notifyCts;
    private long _revision;
    private DateTime _updatedAt = DateTime.UtcNow;

    public GuideUpdateTracker(IHttpClientFactory http, ILogger<GuideUpdateTracker> logger)
    {
        _http = http;
        _logger = logger;
    }

    public void MarkUpdated()
    {
        Interlocked.Increment(ref _revision);
        _updatedAt = DateTime.UtcNow;
        ScheduleNotify();
    }

    public GuideUpdateStatus Snapshot()
        => new(Interlocked.Read(ref _revision), _updatedAt);

    /// <summary>
    /// Stores the Jellyfin base URL the plugin can be reached at from ChannelFlow-Server.
    /// Loopback URLs are rewritten to the inbound request's remote IP so Docker callbacks work.
    /// </summary>
    public string? RegisterPlugin(HttpRequest request, string? jellyfinUrl)
    {
        var resolved = ResolveCallbackUrl(request, jellyfinUrl);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return FinTvRuntime.Current?.Configuration.JellyfinPluginUrl;
        }

        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return resolved;
        }

        if (!string.Equals(plugin.Configuration.JellyfinPluginUrl, resolved, StringComparison.OrdinalIgnoreCase))
        {
            plugin.Configuration.JellyfinPluginUrl = resolved;
            plugin.SaveConfiguration();
            _logger.LogInformation("Jellyfin plugin callback registered at {Url}", resolved);
        }

        return resolved;
    }

    private void ScheduleNotify()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _notifyCts?.Cancel();
            _notifyCts?.Dispose();
            cts = new CancellationTokenSource();
            _notifyCts = cts;
        }

        _ = NotifyAfterDebounceAsync(cts);
    }

    private async Task NotifyAfterDebounceAsync(CancellationTokenSource cts)
    {
        try
        {
            _logger.LogInformation(
                "Guide/playout changed; waiting {Seconds} seconds before asking Jellyfin to refresh listings",
                (int)NotifyDebounce.TotalSeconds);
            await Task.Delay(NotifyDebounce, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await NotifyPluginAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer guide update.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell the Jellyfin plugin to refresh Live TV guide data");
        }
    }

    private async Task NotifyPluginAsync(CancellationToken cancellationToken)
    {
        var baseUrl = FinTvRuntime.Current?.Configuration.JellyfinPluginUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogDebug("Skipping Jellyfin guide refresh: plugin has not registered a callback URL");
            return;
        }

        var apiKey = PluginApiKey.Resolve();
        var client = _http.CreateClient("JellyfinPlugin");
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + PluginRefreshPath);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 300)
            {
                body = body[..300];
            }

            _logger.LogWarning(
                "Jellyfin plugin guide refresh returned {Status}: {Body}",
                (int)response.StatusCode,
                body);
            return;
        }

        _logger.LogInformation("Asked Jellyfin plugin to refresh Live TV guide data");
    }

    internal static string? ResolveCallbackUrl(HttpRequest request, string? jellyfinUrl)
    {
        Uri? provided = null;
        if (!string.IsNullOrWhiteSpace(jellyfinUrl)
            && Uri.TryCreate(jellyfinUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            provided = parsed;
            if (!IsLoopbackHost(parsed.Host))
            {
                return parsed.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }
        }

        var ip = request.HttpContext.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return provided?.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var port = provided is { Port: > 0 } ? provided.Port : 8096;
        var scheme = provided?.Scheme ?? Uri.UriSchemeHttp;
        var host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        return $"{scheme}://{host}:{port}";
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

public sealed record GuideUpdateStatus(long Revision, DateTime UpdatedAt);
