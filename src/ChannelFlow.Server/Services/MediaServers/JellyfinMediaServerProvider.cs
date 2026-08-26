using System.Net.Http.Headers;
using System.Text.Json;
using FinTv.Api;
using FinTv.Domain;

namespace FinTv.Services.MediaServers;

public sealed class JellyfinMediaServerProvider : MediaServerProviderBase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _http;
    private readonly CatalogSyncProgress _progress;

    public JellyfinMediaServerProvider(IHttpClientFactory http, CatalogSyncProgress progress)
    {
        _http = http;
        _progress = progress;
    }

    public override MediaServerKind Kind => MediaServerKind.Jellyfin;

    public override bool CanSync => true;

    public override async Task<MediaServerHealthResult> TestAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = NormalizeToken(connection.AccessToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return new MediaServerHealthResult
                {
                    Ok = false,
                    Message = "Add a Jellyfin API key (Dashboard → API Keys), then Test server."
                };
            }

            var root = NormalizeBaseUrl(connection.BaseUrl);
            var client = CreateClient(connection);
            using var response = await GetAsync(client, root + "/System/Info", token, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var publicName = await TryPublicServerNameAsync(client, root, cancellationToken);
                var prefix = string.IsNullOrWhiteSpace(publicName)
                    ? "Jellyfin"
                    : "Reached " + publicName + " but";
                var reason = (int)response.StatusCode == 401
                    ? " the API key was rejected. Create a new key under Dashboard → API Keys and paste it here. Reverse proxies must forward the Authorization header."
                    : " it returned HTTP " + (int)response.StatusCode + ". Check the URL and API key.";
                return new MediaServerHealthResult
                {
                    Ok = false,
                    Message = prefix + reason
                };
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var name = GetString(doc.RootElement, "ServerName") ?? "Jellyfin";
            var version = GetString(doc.RootElement, "Version");
            var userId = connection.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = await ResolveUserIdAsync(client, root, token, cancellationToken);
            }

            return new MediaServerHealthResult
            {
                Ok = true,
                ServerName = name,
                Version = version,
                UserId = userId,
                Message = "Connected to " + name + (string.IsNullOrWhiteSpace(version) ? "" : " " + version)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MediaServerHealthResult { Ok = false, Message = "Could not reach Jellyfin. " + ex.Message };
        }
    }

    public override async Task<IReadOnlyList<MediaServerRemoteLibrary>> ListLibrariesAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        var root = NormalizeBaseUrl(connection.BaseUrl);
        var token = NormalizeToken(connection.AccessToken);
        var client = CreateClient(connection);
        var userId = connection.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = await ResolveUserIdAsync(client, root, token, cancellationToken)
                ?? throw new InvalidOperationException("Jellyfin API key works but no user was found.");
        }

        using var response = await GetAsync(client, root + "/Users/" + userId + "/Views", token, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MediaServerRemoteLibrary>();
        }

        var libraries = new List<MediaServerRemoteLibrary>();
        foreach (var item in items.EnumerateArray())
        {
            var id = GetString(item, "Id");
            var name = GetString(item, "Name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            libraries.Add(new MediaServerRemoteLibrary
            {
                ExternalId = id,
                Name = name,
                CollectionType = GetString(item, "CollectionType") ?? GetString(item, "CollectionTypeId"),
                ItemCount = item.TryGetProperty("ChildCount", out var count) && count.TryGetInt32(out var n) ? n : null
            });
        }

        return libraries;
    }

    public override async Task<IReadOnlyList<CatalogItemDto>> ImportItemsAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        CancellationToken cancellationToken)
    {
        var items = new List<CatalogItemDto>();
        await ImportIntoAsync(
            connection,
            libraries,
            (batch, _) =>
            {
                items.AddRange(batch);
                return Task.CompletedTask;
            },
            cancellationToken);
        return items;
    }

    public override async Task<int> ImportIntoAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        Func<IReadOnlyList<CatalogItemDto>, CancellationToken, Task> onBatch,
        CancellationToken cancellationToken)
    {
        var enabled = libraries.Where(library => library.SyncEnabled).ToList();
        if (enabled.Count == 0)
        {
            return 0;
        }

        var root = NormalizeBaseUrl(connection.BaseUrl);
        var token = NormalizeToken(connection.AccessToken);
        var client = CreateClient(connection);
        var userId = connection.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = await ResolveUserIdAsync(client, root, token, cancellationToken)
                ?? throw new InvalidOperationException("Jellyfin API key works but no user was found.");
            connection.UserId = userId;
        }

        var imported = 0;
        var libraryIndex = 0;
        foreach (var library in enabled)
        {
            libraryIndex++;
            var start = 0;
            const int page = 200;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _progress.Fetching(library.Name, libraryIndex, enabled.Count, imported, 0);
                var url = root + "/Users/" + Uri.EscapeDataString(userId) + "/Items"
                    + "?ParentId=" + Uri.EscapeDataString(library.ExternalId)
                    + "&Recursive=true&EnableTotalRecordCount=true&EnableImages=false&EnableUserData=false"
                    + "&IncludeItemTypes=Movie,Series,Episode,Audio,MusicVideo,Video"
                    + "&Fields=Path,Overview,PremiereDate,ProviderIds,Chapters,Width,Height,Genres,Tags,Studios,People,"
                    + "ParentId,IndexNumber,ParentIndexNumber,SeriesName,SeasonName,ProductionYear,OfficialRating,"
                    + "CommunityRating,CriticRating,RunTimeTicks,SortName,MediaSources,ChildCount"
                    + "&StartIndex=" + start + "&Limit=" + page;
                using var response = await GetAsync(client, url, token, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!doc.RootElement.TryGetProperty("Items", out var pageItems) || pageItems.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                var rawCount = 0;
                var mapped = new List<CatalogItemDto>();
                foreach (var raw in pageItems.EnumerateArray())
                {
                    rawCount++;
                    var item = MapItem(raw, library, connection.Id);
                    if (item is not null)
                    {
                        mapped.Add(item);
                    }
                }

                if (mapped.Count > 0)
                {
                    await onBatch(mapped, cancellationToken);
                    imported += mapped.Count;
                }

                start += rawCount;
                var total = doc.RootElement.TryGetProperty("TotalRecordCount", out var totalEl) && totalEl.TryGetInt32(out var t)
                    ? t
                    : start;
                _progress.Fetching(library.Name, libraryIndex, enabled.Count, imported, 0);
                if (rawCount == 0 || start >= total)
                {
                    break;
                }
            }
        }

        return imported;
    }

    private HttpClient CreateClient(MediaServerConnection connection)
    {
        var client = _http.CreateClient("MediaServer");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Remove("X-Emby-Authorization");
        var token = NormalizeToken(connection.AccessToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            var auth = MediaBrowserAuthorization(token);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Authorization", auth);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", token);
        }

        return client;
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string url,
        string? token,
        CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || string.IsNullOrWhiteSpace(token))
        {
            return response;
        }

        response.Dispose();
        var joiner = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return await client.GetAsync(url + joiner + "api_key=" + Uri.EscapeDataString(token), cancellationToken);
    }

    private static async Task<string?> TryPublicServerNameAsync(
        HttpClient client,
        string root,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(root + "/System/Info/Public", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return GetString(doc.RootElement, "ServerName");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> ResolveUserIdAsync(
        HttpClient client,
        string root,
        string? token,
        CancellationToken cancellationToken)
    {
        using var response = await GetAsync(client, root + "/Users", token, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;
        foreach (var user in doc.RootElement.EnumerateArray())
        {
            var id = GetString(user, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            first ??= id;
            if (user.TryGetProperty("Policy", out var policy)
                && policy.TryGetProperty("IsAdministrator", out var admin)
                && admin.ValueKind == JsonValueKind.True)
            {
                return id;
            }
        }

        return first;
    }

    private static string MediaBrowserAuthorization(string token)
        => "MediaBrowser Client=\"ChannelFlow\", Device=\"ChannelFlow-Server\", DeviceId=\"channelflow\", Version=\"1.0\", Token=\""
           + token.Replace("\"", "", StringComparison.Ordinal) + "\"";

    private static string? NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return token.Trim().Trim('"').Trim();
    }

    private static CatalogItemDto? MapItem(JsonElement raw, MediaServerLibrary library, Guid connectionId)
    {
        if (!TryGuid(GetString(raw, "Id"), out var id))
        {
            return null;
        }

        var type = GetString(raw, "Type") ?? "";
        var kind = type.Trim().ToLowerInvariant() switch
        {
            "movie" => BaseItemKind.Movie,
            "series" => BaseItemKind.Series,
            "episode" => BaseItemKind.Episode,
            "audio" => BaseItemKind.Audio,
            "musicvideo" => BaseItemKind.MusicVideo,
            "video" => BaseItemKind.Video,
            "season" => BaseItemKind.Season,
            _ => (BaseItemKind?)null
        };
        if (kind is null)
        {
            return null;
        }

        TryGuid(GetString(raw, "ParentId"), out var parentId);
        TryGuid(GetString(raw, "SeriesId"), out var seriesId);
        TryGuid(GetString(raw, "SeasonId"), out var seasonId);
        Guid.TryParse(library.ExternalId, out var libraryGuid);

        int? width = GetInt(raw, "Width");
        int? height = GetInt(raw, "Height");
        ReadVideoSize(raw, ref width, ref height);

        return new CatalogItemDto
        {
            Id = id,
            Name = GetString(raw, "Name"),
            SortName = GetString(raw, "SortName"),
            Overview = GetString(raw, "Overview"),
            Kind = kind.Value,
            Path = GetString(raw, "Path"),
            ParentId = parentId == Guid.Empty ? null : parentId,
            SeriesId = seriesId == Guid.Empty ? null : seriesId,
            SeriesName = GetString(raw, "SeriesName"),
            ProductionYear = GetInt(raw, "ProductionYear"),
            PremiereDate = GetDate(raw, "PremiereDate"),
            OfficialRating = GetString(raw, "OfficialRating"),
            CommunityRating = GetFloat(raw, "CommunityRating"),
            CriticRating = GetFloat(raw, "CriticRating"),
            RuntimeTicks = GetLong(raw, "RunTimeTicks"),
            IndexNumber = GetInt(raw, "IndexNumber"),
            ParentIndexNumber = GetInt(raw, "ParentIndexNumber"),
            LibraryId = libraryGuid == Guid.Empty ? library.Id : libraryGuid,
            LibraryName = library.Name,
            CollectionType = library.CollectionType,
            SourceConnectionId = connectionId,
            SeasonId = seasonId == Guid.Empty ? null : seasonId,
            SeasonName = GetString(raw, "SeasonName"),
            Width = width,
            Height = height,
            Genres = ReadStringArray(raw, "Genres"),
            Tags = ReadStringArray(raw, "Tags"),
            Studios = ReadStringNames(raw, "Studios"),
            People = ReadPeople(raw),
            ProviderIds = ReadStringMap(raw, "ProviderIds"),
            Chapters = ReadChapters(raw),
            Artists = ReadStringArray(raw, "Artists")
        };
    }

    private static void ReadVideoSize(JsonElement raw, ref int? width, ref int? height)
    {
        if (width is > 0 && height is > 0)
        {
            return;
        }

        if (!raw.TryGetProperty("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var source in sources.EnumerateArray())
        {
            if (!source.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (!string.Equals(GetString(stream, "Type"), "Video", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                width ??= GetInt(stream, "Width");
                height ??= GetInt(stream, "Height");
                return;
            }
        }
    }

    private static List<CatalogChapterDto>? ReadChapters(JsonElement raw)
    {
        if (!raw.TryGetProperty("Chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<CatalogChapterDto>();
        foreach (var chapter in chapters.EnumerateArray())
        {
            list.Add(new CatalogChapterDto
            {
                Name = GetString(chapter, "Name"),
                StartPositionTicks = GetLong(chapter, "StartPositionTicks") ?? 0
            });
        }

        return list;
    }

    private static List<CatalogPersonDto>? ReadPeople(JsonElement raw)
    {
        if (!raw.TryGetProperty("People", out var people) || people.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<CatalogPersonDto>();
        foreach (var person in people.EnumerateArray())
        {
            var name = GetString(person, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new CatalogPersonDto
            {
                Name = name,
                Type = GetString(person, "Type"),
                Role = GetString(person, "Role")
            });
        }

        return list;
    }

    private static List<string>? ReadStringArray(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : GetString(item, "Name"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    private static List<string>? ReadStringNames(JsonElement raw, string name) => ReadStringArray(raw, name);

    private static Dictionary<string, string>? ReadStringMap(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in value.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
            {
                map[prop.Name] = prop.Value.GetString()!;
            }
        }

        return map;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : null;

    private static long? GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : null;

    private static float? GetFloat(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.TryGetSingle(out var n) ? n : null;

    private static DateTime? GetDate(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.TryGetDateTime(out var n) ? n : null;

    private static bool TryGuid(string? value, out Guid id)
    {
        if (Guid.TryParse(value, out id) && id != Guid.Empty)
        {
            return true;
        }

        id = Guid.Empty;
        return false;
    }
}
