using System.Net.Http.Headers;
using System.Text.Json;
using FinTv.Api;
using FinTv.Domain;

namespace FinTv.Services.MediaServers;

public interface IMediaServerProvider
{
    MediaServerKind Kind { get; }

    bool CanSync { get; }

    Task<MediaServerHealthResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaServerRemoteLibrary>> ListLibrariesAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogItemDto>> ImportItemsAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        CancellationToken cancellationToken);

    Task<int> ImportIntoAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        Func<IReadOnlyList<CatalogItemDto>, CancellationToken, Task> onBatch,
        CancellationToken cancellationToken);
}

public abstract class MediaServerProviderBase : IMediaServerProvider
{
    public abstract MediaServerKind Kind { get; }

    public virtual bool CanSync => false;

    public abstract Task<MediaServerHealthResult> TestAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken);

    public virtual Task<IReadOnlyList<MediaServerRemoteLibrary>> ListLibrariesAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MediaServerRemoteLibrary>>(Array.Empty<MediaServerRemoteLibrary>());

    public virtual Task<IReadOnlyList<CatalogItemDto>> ImportItemsAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CatalogItemDto>>(Array.Empty<CatalogItemDto>());

    public virtual async Task<int> ImportIntoAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        Func<IReadOnlyList<CatalogItemDto>, CancellationToken, Task> onBatch,
        CancellationToken cancellationToken)
    {
        var items = await ImportItemsAsync(connection, libraries, cancellationToken);
        if (items.Count > 0)
        {
            await onBatch(items, cancellationToken);
        }

        return items.Count;
    }

    protected static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Server URL is required.");
        }

        return url.Trim().TrimEnd('/');
    }
}

public sealed class PlaceholderMediaServerProvider : MediaServerProviderBase
{
    public PlaceholderMediaServerProvider(MediaServerKind kind)
    {
        Kind = kind;
    }

    public override MediaServerKind Kind { get; }

    public override async Task<MediaServerHealthResult> TestAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        if (Kind == MediaServerKind.Emby)
        {
            return await PingEmbyAsync(connection, cancellationToken);
        }

        if (Kind == MediaServerKind.Plex)
        {
            return await PingPlexAsync(connection, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(connection.BaseUrl))
        {
            return Fail("Enter a URL for this server.");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var response = await client.GetAsync(NormalizeBaseUrl(connection.BaseUrl), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Fail("Reached the host but it returned HTTP " + (int)response.StatusCode + ".");
            }

            return new MediaServerHealthResult
            {
                Ok = true,
                Message = Kind + " connection saved. Catalog sync for this server is not available yet."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("Could not reach " + connection.BaseUrl + ". " + ex.Message);
        }
    }

    private static async Task<MediaServerHealthResult> PingEmbyAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = NormalizeBaseUrl(connection.BaseUrl);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var request = new HttpRequestMessage(HttpMethod.Get, root + "/System/Info");
            if (!string.IsNullOrWhiteSpace(connection.AccessToken))
            {
                var token = connection.AccessToken.Trim();
                var auth = "MediaBrowser Client=\"ChannelFlow\", Device=\"ChannelFlow-Server\", DeviceId=\"channelflow\", Version=\"1.0\", Token=\"" + token.Replace("\"", "", StringComparison.Ordinal) + "\"";
                request.Headers.TryAddWithoutValidation("Authorization", auth);
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", auth);
                request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Fail("Emby returned HTTP " + (int)response.StatusCode + ". Check the URL and API key.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var name = doc.RootElement.TryGetProperty("ServerName", out var n) ? n.GetString() : "Emby";
            var version = doc.RootElement.TryGetProperty("Version", out var v) ? v.GetString() : null;
            return new MediaServerHealthResult
            {
                Ok = true,
                ServerName = name,
                Version = version,
                Message = "Connected to " + name + ". Catalog sync for Emby is not available yet."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("Could not reach Emby. " + ex.Message);
        }
    }

    private static async Task<MediaServerHealthResult> PingPlexAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = NormalizeBaseUrl(connection.BaseUrl);
            var url = root + "/identity";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(connection.AccessToken))
            {
                request.Headers.TryAddWithoutValidation("X-Plex-Token", connection.AccessToken);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Fail("Plex returned HTTP " + (int)response.StatusCode + ". Check the URL and token.");
            }

            return new MediaServerHealthResult
            {
                Ok = true,
                ServerName = "Plex",
                Message = "Reached Plex. Catalog sync for Plex is not available yet."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("Could not reach Plex. " + ex.Message);
        }
    }

    private static MediaServerHealthResult Fail(string message)
        => new() { Ok = false, Message = message };
}
