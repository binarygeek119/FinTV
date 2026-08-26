using System.Text.Json;
using FinTv;
using FinTv.Api;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services.MediaServers;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public sealed class MediaServerService
{
    private readonly FinTvDbContext _db;
    private readonly CatalogIngestService _ingest;
    private readonly PathRemapService _remap;
    private readonly CatalogSyncProgress _progress;
    private readonly IReadOnlyDictionary<MediaServerKind, IMediaServerProvider> _providers;

    private readonly ILogger<MediaServerService> _logger;

    public MediaServerService(
        FinTvDbContext db,
        CatalogIngestService ingest,
        PathRemapService remap,
        CatalogSyncProgress progress,
        IEnumerable<IMediaServerProvider> providers,
        ILogger<MediaServerService> logger)
    {
        _db = db;
        _ingest = ingest;
        _remap = remap;
        _progress = progress;
        _providers = providers.ToDictionary(p => p.Kind);
        _logger = logger;
    }

    public async Task<List<object>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.MediaServerConnections
            .AsNoTracking()
            .Include(c => c.Libraries)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
        return rows.Select(Public).ToList();
    }

    public async Task<object> CreateAsync(MediaServerWriteRequest request, CancellationToken cancellationToken)
    {
        var row = new MediaServerConnection();
        Apply(row, request, creating: true);
        var max = await _db.MediaServerConnections.MaxAsync(c => (int?)c.SortOrder, cancellationToken) ?? -1;
        row.SortOrder = max + 1;
        _db.MediaServerConnections.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return Public(row);
    }

    public async Task<object?> UpdateAsync(Guid id, MediaServerWriteRequest request, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        Apply(row, request, creating: false);
        await _db.SaveChangesAsync(cancellationToken);
        return Public(row);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (row is null)
        {
            return false;
        }

        _db.MediaServerConnections.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<object> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");
        var result = await Provider(row.Kind).TestAsync(row, cancellationToken);
        row.LastHealthUtc = result.CheckedAt;
        row.LastHealthOk = result.Ok;
        row.LastHealthMessage = result.Message;
        if (!string.IsNullOrWhiteSpace(result.UserId))
        {
            row.UserId = result.UserId;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new { health = result, server = Public(row) };
    }

    public async Task<IReadOnlyList<MediaServerRemoteLibrary>> BrowseLibrariesAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await RequireAsync(id, cancellationToken);
        return await Provider(row.Kind).ListLibrariesAsync(row, cancellationToken);
    }

    public async Task<object> RefreshLibrariesAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");
        var remote = await Provider(row.Kind).ListLibrariesAsync(row, cancellationToken);
        var existing = row.Libraries.ToDictionary(l => l.ExternalId, StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var lib in remote)
        {
            if (!existing.TryGetValue(lib.ExternalId, out var local))
            {
                local = new MediaServerLibrary
                {
                    Id = Guid.NewGuid(),
                    ConnectionId = row.Id,
                    ExternalId = lib.ExternalId,
                    SyncEnabled = true
                };
                row.Libraries.Add(local);
                existing[lib.ExternalId] = local;
            }

            local.Name = lib.Name;
            local.CollectionType = lib.CollectionType;
            local.ItemCount = lib.ItemCount ?? local.ItemCount;
            local.SortOrder = order++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Public(row);
    }

    public async Task<object> SaveLibrariesAsync(
        Guid id,
        IReadOnlyList<MediaServerLibrarySyncRequest> libraries,
        CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");
        var byId = row.Libraries.ToDictionary(l => l.Id);
        foreach (var update in libraries)
        {
            if (!byId.TryGetValue(update.Id, out var local))
            {
                continue;
            }

            local.SyncEnabled = update.SyncEnabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        SyncLegacyJellyfinSettings();
        return Public(row);
    }

    public async Task<List<(Guid Id, string Name)>> ListEnabledSyncableAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.MediaServerConnections.AsNoTracking()
            .Where(c => c.Enabled)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Kind })
            .ToListAsync(cancellationToken);
        return rows
            .Where(row => _providers.TryGetValue(row.Kind, out var provider) && provider.CanSync)
            .Select(row => (row.Id, row.Name))
            .ToList();
    }

    public bool IsSyncRunning => _progress.IsRunning;

    public async Task EnsureCanSyncAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");
        var provider = Provider(row.Kind);
        if (!provider.CanSync)
        {
            throw new InvalidOperationException(row.Kind + " catalog sync is not available yet.");
        }

        if (_progress.IsRunning)
        {
            throw new InvalidOperationException("A catalog sync is already running.");
        }
    }

    public async Task<MediaServerSyncResult> SyncAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");
        var provider = Provider(row.Kind);
        if (!provider.CanSync)
        {
            throw new InvalidOperationException(row.Kind + " catalog sync is not available yet.");
        }

        if (!_progress.TryStart(row.Name))
        {
            throw new InvalidOperationException("A catalog sync is already running.");
        }

        try
        {
            if (row.Libraries.Count == 0)
            {
                _progress.Libraries(row.Name, 0);
                await RefreshLibrariesAsync(id, cancellationToken);
                row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstAsync(c => c.Id == id, cancellationToken);
            }

            var enabled = row.Libraries.Count(library => library.SyncEnabled);
            _progress.Libraries(row.Name, enabled);
            _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));
            var incomingIds = new HashSet<Guid>();
            var libraryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            await provider.ImportIntoAsync(
                row,
                row.Libraries.ToList(),
                async (batch, ct) =>
                {
                    count += await _ingest.UpsertAsync(batch, row.Id, markMissing: false, ct);
                    foreach (var item in batch)
                    {
                        incomingIds.Add(item.Id);
                        var key = item.LibraryName ?? "";
                        libraryCounts[key] = libraryCounts.GetValueOrDefault(key) + 1;
                    }
                },
                cancellationToken);

            if (count == 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                SyncLegacyJellyfinSettings();
                _progress.Complete(0);
                _logger.LogWarning("Catalog sync for {Server} imported 0 items; existing catalog was left unchanged", row.Name);
                return new MediaServerSyncResult
                {
                    Count = 0,
                    Server = Public(row),
                    Message = "Jellyfin returned no items. Existing catalog was left unchanged."
                };
            }

            await _ingest.FinishMissingAsync(incomingIds, row.Id, cancellationToken);
            row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstAsync(c => c.Id == id, cancellationToken);
            foreach (var library in row.Libraries.Where(l => l.SyncEnabled))
            {
                library.ItemCount = libraryCounts.GetValueOrDefault(library.Name);
            }

            await _db.SaveChangesAsync(cancellationToken);
            SyncLegacyJellyfinSettings();
            _progress.Complete(count);
            _logger.LogInformation("Catalog sync for {Server} saved {Count} items", row.Name, count);
            return new MediaServerSyncResult { Count = count, Server = Public(row) };
        }
        catch (Exception ex)
        {
            _progress.Fail(ex.Message);
            throw;
        }
    }

    public object GetSyncProgress() => _progress.Snapshot();

    public Task<IReadOnlyList<PathMapping>> GetMappingsAsync(Guid id, CancellationToken cancellationToken)
        => _remap.GetAllAsync(id, cancellationToken);

    public Task ReplaceMappingsAsync(Guid id, IReadOnlyList<PathMapping> mappings, CancellationToken cancellationToken)
        => _remap.ReplaceAllAsync(mappings, id, cancellationToken);

    public Task<object> TestMappingsAsync(Guid id, int sample, CancellationToken cancellationToken)
        => _remap.TestAsync(sample, id, cancellationToken);

    public async Task<object> GetCatalogAsync(Guid? connectionId, MediaServerKind? kind, CancellationToken cancellationToken)
    {
        var query = _db.MediaItems.AsNoTracking().Where(i => !i.IsMissing);
        if (connectionId is Guid cid)
        {
            query = query.Where(i => i.SourceConnectionId == cid);
        }
        else if (kind is MediaServerKind k)
        {
            var ids = await _db.MediaServerConnections.AsNoTracking()
                .Where(c => c.Kind == k)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            query = query.Where(i => i.SourceConnectionId != null && ids.Contains(i.SourceConnectionId.Value));
        }

        var newsQuery = query.Where(i =>
            (i.CollectionType != null && (
                i.CollectionType.ToLower() == "homevideos"
                || i.CollectionType.ToLower() == "homemovies"))
            || (i.LibraryName != null && EF.Functions.ILike(i.LibraryName, "%Past Tense%")));
        var tv = await LoadCatalogBucketAsync(
            query.Where(i =>
                (i.Kind == BaseItemKind.Episode || i.Kind == BaseItemKind.Series)
                && (i.CollectionType == null || (
                    i.CollectionType.ToLower() != "homevideos"
                    && i.CollectionType.ToLower() != "homemovies"))
                && (i.LibraryName == null || !EF.Functions.ILike(i.LibraryName, "%Past Tense%"))),
            cancellationToken);
        var movies = await LoadCatalogBucketAsync(
            query.Where(i =>
                i.Kind == BaseItemKind.Movie
                && (i.LibraryName == null || !EF.Functions.ILike(i.LibraryName, "%Past Tense%"))),
            cancellationToken);
        var music = await LoadCatalogBucketAsync(
            query.Where(i => i.Kind == BaseItemKind.Audio),
            cancellationToken);
        var musicVideos = await LoadCatalogBucketAsync(
            query.Where(i => i.Kind == BaseItemKind.MusicVideo),
            cancellationToken);
        var news = await LoadCatalogBucketAsync(newsQuery, cancellationToken);

        return new
        {
            tvShows = tv.Rows,
            movies = movies.Rows,
            music = music.Rows,
            musicVideos = musicVideos.Rows,
            pastTenseNews = news.Rows,
            totals = new
            {
                tvShows = tv.Total,
                movies = movies.Total,
                music = music.Total,
                musicVideos = musicVideos.Total,
                pastTenseNews = news.Total
            }
        };
    }

    private const int CatalogPreviewLimit = 400;

    private async Task<(int Total, List<object> Rows)> LoadCatalogBucketAsync(
        IQueryable<MediaItem> query,
        CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.Name)
            .Take(CatalogPreviewLimit)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Kind,
                item.Overview,
                item.OfficialRating,
                item.CommunityRating,
                item.Runtime,
                item.Path,
                item.SeriesName,
                item.LibraryName,
                item.PeopleJson,
                item.ProviderIdsJson,
                item.AspectRatio
            })
            .ToListAsync(cancellationToken);

        return (total, rows.Select(row => (object)new
        {
            row.Id,
            row.Name,
            row.Kind,
            row.Overview,
            row.OfficialRating,
            row.CommunityRating,
            row.Runtime,
            row.Path,
            row.SeriesName,
            row.LibraryName,
            row.AspectRatio,
            format = row.AspectRatio,
            chapters = "",
            rating = row.OfficialRating ?? row.CommunityRating?.ToString("0.#"),
            plot = row.Overview,
            stars = JoinJsonNames(row.PeopleJson, "Name"),
            ids = JoinJsonMap(row.ProviderIdsJson)
        }).ToList());
    }

    public async Task<object> GetRemovedAsync(CancellationToken cancellationToken)
    {
        var servers = await _db.MediaServerConnections.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.Kind })
            .ToListAsync(cancellationToken);
        var names = servers.ToDictionary(s => s.Id, s => s.Name);

        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => i.IsMissing)
            .OrderBy(i => i.Name)
            .Select(i => new
            {
                i.Id,
                i.Name,
                i.Kind,
                i.Path,
                i.LibraryName,
                i.SourceConnectionId,
                i.MissingSince
            })
            .ToListAsync(cancellationToken);

        return new
        {
            count = items.Count,
            items = items.Select(i => new
            {
                i.Id,
                i.Name,
                i.Kind,
                i.Path,
                i.LibraryName,
                i.SourceConnectionId,
                i.MissingSince,
                serverName = i.SourceConnectionId is Guid sid && names.TryGetValue(sid, out var name)
                    ? name
                    : "Unknown"
            })
        };
    }

    private IMediaServerProvider Provider(MediaServerKind kind)
        => _providers.TryGetValue(kind, out var provider)
            ? provider
            : throw new InvalidOperationException("Unsupported media server type: " + kind);

    private async Task<MediaServerConnection> RequireAsync(Guid id, CancellationToken cancellationToken)
        => await _db.MediaServerConnections.Include(c => c.Libraries).FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");

    private static void Apply(MediaServerConnection row, MediaServerWriteRequest request, bool creating)
    {
        if (creating)
        {
            if (!Enum.TryParse<MediaServerKind>(request.Kind, true, out var kind))
            {
                throw new InvalidOperationException("Unknown media server type.");
            }

            row.Kind = kind;
        }

        row.Name = string.IsNullOrWhiteSpace(request.Name) ? row.Kind.ToString() : request.Name.Trim();
        row.BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? row.BaseUrl : request.BaseUrl.Trim().TrimEnd('/');
        if (request.AccessToken is not null)
        {
            if (request.AccessToken.Length > 0)
            {
                row.AccessToken = request.AccessToken.Trim().Trim('"').Trim();
            }
        }

        row.SidecarRoot = string.IsNullOrWhiteSpace(request.SidecarRoot) ? request.SidecarRoot : request.SidecarRoot.Trim();
        row.Enabled = request.Enabled ?? row.Enabled;
    }

    private object Public(MediaServerConnection row)
        => new
        {
            row.Id,
            kind = row.Kind.ToString().ToLowerInvariant(),
            row.Name,
            row.BaseUrl,
            hasToken = !string.IsNullOrWhiteSpace(row.AccessToken),
            row.SidecarRoot,
            row.Enabled,
            row.SortOrder,
            row.LastHealthUtc,
            row.LastHealthOk,
            row.LastHealthMessage,
            canSync = _providers.TryGetValue(row.Kind, out var p) && p.CanSync,
            libraries = row.Libraries
                .OrderBy(l => l.SortOrder)
                .Select(l => new
                {
                    l.Id,
                    l.ExternalId,
                    l.Name,
                    l.CollectionType,
                    l.SyncEnabled,
                    l.ItemCount,
                    group = LibraryGroup(l.CollectionType)
                })
        };

    private void SyncLegacyJellyfinSettings()
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return;
        }

        var jellyfin = _db.MediaServerConnections
            .Include(c => c.Libraries)
            .Where(c => c.Kind == MediaServerKind.Jellyfin)
            .SelectMany(c => c.Libraries)
            .ToList();
        if (jellyfin.Count == 0)
        {
            return;
        }

        plugin.Configuration.JellyfinLibraries.Libraries = jellyfin.Select(lib =>
        {
            Guid.TryParse(lib.ExternalId, out var id);
            return new Configuration.JellyfinLibraryInfo
            {
                Id = id == Guid.Empty ? lib.Id : id,
                Name = lib.Name,
                CollectionType = lib.CollectionType
            };
        }).ToList();

        plugin.Configuration.JellyfinLibraries.TvLibraryIds = Ids(jellyfin, "tv");
        plugin.Configuration.JellyfinLibraries.MovieLibraryIds = Ids(jellyfin, "movies");
        plugin.Configuration.JellyfinLibraries.MusicLibraryIds = Ids(jellyfin, "music");
        plugin.Configuration.JellyfinLibraries.MusicVideoLibraryIds = Ids(jellyfin, "musicvideos");
        plugin.Configuration.JellyfinLibraries.HomeVideoLibraryIds = Ids(jellyfin, "news");
        plugin.SaveConfiguration();
    }

    private static List<Guid> Ids(IEnumerable<MediaServerLibrary> libraries, string group)
        => libraries
            .Where(lib => lib.SyncEnabled && LibraryGroup(lib.CollectionType) == group)
            .Select(lib => Guid.TryParse(lib.ExternalId, out var id) && id != Guid.Empty ? id : lib.Id)
            .ToList();

    private static string JoinJsonNames(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json) || json is "[]" or "{}")
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return "";
            }

            var names = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var value = el.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        names.Add(value);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty(property, out var nameEl))
                {
                    var value = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        names.Add(value);
                    }
                }
            }

            return string.Join(", ", names.Take(8));
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string JoinJsonMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json is "{}")
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(prop.Name + ":" + value);
                }
            }

            return string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static string LibraryGroup(string? collectionType)
    {
        var value = collectionType?.Trim().ToLowerInvariant() ?? "";
        if (value is "tvshows" or "tv" or "series")
        {
            return "tv";
        }

        if (value is "movies" or "movie")
        {
            return "movies";
        }

        if (value is "musicvideos" or "musicvideo")
        {
            return "musicvideos";
        }

        if (value is "music")
        {
            return "music";
        }

        if (value is "homevideos" or "homevideos" or "homemovies")
        {
            return "news";
        }

        return "other";
    }
}

public sealed class MediaServerSyncResult
{
    public int Count { get; init; }

    public object? Server { get; init; }

    public string? Message { get; init; }
}

public sealed class MediaServerWriteRequest
{
    public string? Kind { get; set; }

    public string? Name { get; set; }

    public string? BaseUrl { get; set; }

    public string? AccessToken { get; set; }

    public string? SidecarRoot { get; set; }

    public bool? Enabled { get; set; }
}

public sealed class MediaServerLibrarySyncRequest
{
    public Guid Id { get; set; }

    public bool SyncEnabled { get; set; }
}
