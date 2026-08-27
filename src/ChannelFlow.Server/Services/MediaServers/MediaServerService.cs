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
    private readonly CatalogChapterProbeService _chapters;
    private readonly CatalogSyncProgress _progress;
    private readonly IReadOnlyDictionary<MediaServerKind, IMediaServerProvider> _providers;

    private readonly ILogger<MediaServerService> _logger;

    public MediaServerService(
        FinTvDbContext db,
        CatalogIngestService ingest,
        PathRemapService remap,
        CatalogChapterProbeService chapters,
        CatalogSyncProgress progress,
        IEnumerable<IMediaServerProvider> providers,
        ILogger<MediaServerService> logger)
    {
        _db = db;
        _ingest = ingest;
        _remap = remap;
        _chapters = chapters;
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

        await SaveChangesRetryingAsync(cancellationToken);
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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await RefreshLibrariesOnceAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                _db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (attempt < 2 && IsUniqueViolation(ex))
            {
                _db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not refresh libraries because they changed at the same time. Try again.");
    }

    private async Task<object> RefreshLibrariesOnceAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.MediaServerConnections.Include(c => c.Libraries).FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Media server not found.");
        var remote = await Provider(row.Kind).ListLibrariesAsync(row, cancellationToken);
        var existing = row.Libraries
            .GroupBy(library => library.ExternalId ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var lib in remote)
        {
            var externalId = (lib.ExternalId ?? "").Trim();
            if (externalId.Length == 0 || !seen.Add(externalId))
            {
                continue;
            }

            if (!existing.TryGetValue(externalId, out var local))
            {
                local = new MediaServerLibrary
                {
                    Id = Guid.NewGuid(),
                    ConnectionId = row.Id,
                    ExternalId = externalId,
                    SyncEnabled = true
                };
                row.Libraries.Add(local);
                _db.MediaServerLibraries.Add(local);
                existing[externalId] = local;
            }

            local.ExternalId = externalId;
            local.Name = lib.Name;
            local.CollectionType = NormalizeCollectionType(lib.CollectionType, lib.Name);
            local.ItemCount = lib.ItemCount ?? local.ItemCount;
            local.SortOrder = order++;
        }

        await SaveChangesRetryingAsync(cancellationToken);
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

        await SaveChangesRetryingAsync(cancellationToken);
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
                await SaveChangesRetryingAsync(cancellationToken);
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

            await SaveChangesRetryingAsync(cancellationToken);
            SyncLegacyJellyfinSettings();
            await ProbeChaptersAfterImportAsync(incomingIds, cancellationToken);
            _progress.Complete(count);
            _logger.LogInformation("Catalog sync for {Server} saved {Count} items", row.Name, count);
            return new MediaServerSyncResult { Count = count, Server = Public(row) };
        }
        catch (Exception ex)
        {
            _progress.Fail(
                ex is DbUpdateConcurrencyException
                    ? "Catalog changed while saving. Try sync again."
                    : ex.Message);
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
        var query = await ScopedCatalogQueryAsync(connectionId, kind, cancellationToken);

        var newsQuery = WhereNewsCatalog(query);
        var notNews = WhereNotNewsCatalog(query);
        var tvQuery = notNews.Where(i => i.Kind == BaseItemKind.Episode || i.Kind == BaseItemKind.Series);
        var tv = await LoadTvCatalogAsync(tvQuery, cancellationToken);
        var movies = await LoadCatalogBucketAsync(
            notNews.Where(i => i.Kind == BaseItemKind.Movie),
            cancellationToken);
        var music = await LoadArtistCatalogAsync(
            query.Where(i => i.Kind == BaseItemKind.Audio),
            cancellationToken);
        var musicVideos = await LoadArtistCatalogAsync(
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

    public async Task<object> GetCatalogEpisodesAsync(
        Guid? connectionId,
        MediaServerKind? kind,
        Guid? seriesId,
        string? seriesName,
        CancellationToken cancellationToken)
    {
        var query = await ScopedCatalogQueryAsync(connectionId, kind, cancellationToken);
        query = query.Where(item => item.Kind == BaseItemKind.Episode);
        query = WhereNotNewsCatalog(query);

        var name = seriesName?.Trim();
        if (seriesId is Guid id && id != Guid.Empty)
        {
            query = query.Where(item => item.SeriesId == id);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(item => item.SeriesName == name && (item.SeriesId == null || item.SeriesId == Guid.Empty));
        }
        else
        {
            return new { episodes = Array.Empty<object>() };
        }

        var rows = await query
            .OrderBy(item => item.ParentIndexNumber)
            .ThenBy(item => item.IndexNumber)
            .ThenBy(item => item.Name)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Kind,
                item.Overview,
                item.OfficialRating,
                item.CommunityRating,
                item.Runtime,
                item.RuntimeTicks,
                item.Path,
                item.SeriesName,
                item.SeasonName,
                item.IndexNumber,
                item.ParentIndexNumber,
                item.LibraryName,
                item.PeopleJson,
                item.ProviderIdsJson,
                item.AspectRatio,
                item.TrueAspectRatio,
                item.Width,
                item.Height
            })
            .ToListAsync(cancellationToken);

        return new
        {
            episodes = rows.Select(row => new
            {
                row.Id,
                name = row.Name,
                code = EpisodeCode(row.ParentIndexNumber, row.IndexNumber, row.SeasonName),
                season = row.ParentIndexNumber,
                seasonName = row.SeasonName,
                episode = row.IndexNumber,
                kind = row.Kind,
                runtime = string.IsNullOrWhiteSpace(row.Runtime) ? FormatRuntime(row.RuntimeTicks) : row.Runtime,
                format = VideoAspectFormat.Prefer(row.TrueAspectRatio, row.AspectRatio, row.Width, row.Height) ?? row.AspectRatio,
                rating = FormatRating(row.OfficialRating, row.CommunityRating),
                plot = TruncatePlot(row.Overview),
                stars = JoinJsonNames(row.PeopleJson, "Name"),
                path = row.Path ?? string.Empty,
                ids = JoinJsonMap(row.ProviderIdsJson)
            }).ToList()
        };
    }

    public Task<object> GetCatalogMusicAsync(
        Guid? connectionId,
        MediaServerKind? kind,
        string? artist,
        CancellationToken cancellationToken)
        => GetCatalogArtistItemsAsync(connectionId, kind, BaseItemKind.Audio, artist, cancellationToken);

    public Task<object> GetCatalogMusicVideosAsync(
        Guid? connectionId,
        MediaServerKind? kind,
        string? artist,
        CancellationToken cancellationToken)
        => GetCatalogArtistItemsAsync(connectionId, kind, BaseItemKind.MusicVideo, artist, cancellationToken);

    private async Task<object> GetCatalogArtistItemsAsync(
        Guid? connectionId,
        MediaServerKind? kind,
        BaseItemKind itemKind,
        string? artist,
        CancellationToken cancellationToken)
    {
        var wanted = artist?.Trim();
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return new { items = Array.Empty<object>() };
        }

        var query = await ScopedCatalogQueryAsync(connectionId, kind, cancellationToken);
        query = query.Where(item => item.Kind == itemKind);

        var rows = await query
            .OrderBy(item => item.Name)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Kind,
                item.Overview,
                item.OfficialRating,
                item.CommunityRating,
                item.Runtime,
                item.RuntimeTicks,
                item.Path,
                item.Album,
                item.LibraryName,
                item.PeopleJson,
                item.ProviderIdsJson,
                item.ArtistsJson,
                item.AlbumArtistsJson,
                item.AspectRatio,
                item.TrueAspectRatio,
                item.Width,
                item.Height
            })
            .ToListAsync(cancellationToken);

        return new
        {
            items = rows
                .Where(row => string.Equals(
                    PrimaryArtist(row.ArtistsJson, row.AlbumArtistsJson, row.PeopleJson),
                    wanted,
                    StringComparison.OrdinalIgnoreCase))
                .Select(row => new
                {
                    id = row.Id,
                    name = row.Name,
                    album = row.Album ?? string.Empty,
                    kind = row.Kind,
                    runtime = string.IsNullOrWhiteSpace(row.Runtime) ? FormatRuntime(row.RuntimeTicks) : row.Runtime,
                    format = VideoAspectFormat.Prefer(row.TrueAspectRatio, row.AspectRatio, row.Width, row.Height) ?? row.AspectRatio,
                    rating = FormatRating(row.OfficialRating, row.CommunityRating),
                    plot = TruncatePlot(row.Overview),
                    stars = CatalogStars(row.PeopleJson, row.ArtistsJson),
                    path = row.Path ?? string.Empty,
                    ids = JoinJsonMap(row.ProviderIdsJson)
                })
                .ToList()
        };
    }

    private const int CatalogPreviewLimit = 400;

    private async Task<IQueryable<MediaItem>> ScopedCatalogQueryAsync(
        Guid? connectionId,
        MediaServerKind? kind,
        CancellationToken cancellationToken)
    {
        var query = _db.MediaItems.AsNoTracking().Where(i => !i.IsMissing);
        if (connectionId is Guid cid)
        {
            return query.Where(i => i.SourceConnectionId == cid);
        }

        if (kind is MediaServerKind k)
        {
            var ids = await _db.MediaServerConnections.AsNoTracking()
                .Where(c => c.Kind == k)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            return query.Where(i => i.SourceConnectionId != null && ids.Contains(i.SourceConnectionId.Value));
        }

        return query;
    }

    private async Task<(int Total, List<object> Rows)> LoadTvCatalogAsync(
        IQueryable<MediaItem> query,
        CancellationToken cancellationToken)
    {
        var seriesRows = await query.Where(item => item.Kind == BaseItemKind.Series)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Overview,
                item.OfficialRating,
                item.CommunityRating,
                item.LibraryName,
                item.PeopleJson,
                item.ProviderIdsJson
            })
            .ToListAsync(cancellationToken);

        var countsBySeriesId = await query.Where(item => item.Kind == BaseItemKind.Episode && item.SeriesId != null)
            .GroupBy(item => item.SeriesId!.Value)
            .Select(group => new
            {
                Id = group.Key,
                Count = group.Count(),
                Name = group.Max(item => item.SeriesName)
            })
            .ToListAsync(cancellationToken);

        var countsByName = await query.Where(item =>
                item.Kind == BaseItemKind.Episode && (item.SeriesId == null || item.SeriesId == Guid.Empty))
            .GroupBy(item => item.SeriesName ?? "")
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var shows = new List<(string Name, object Row)>();
        var seenIds = new HashSet<Guid>();
        var countById = countsBySeriesId.ToDictionary(row => row.Id, row => row);

        foreach (var series in seriesRows)
        {
            seenIds.Add(series.Id);
            var count = countById.TryGetValue(series.Id, out var grouped) ? grouped.Count : 0;
            shows.Add((series.Name, TvShowRow(
                series.Id,
                series.Id,
                series.Name,
                series.Name,
                count,
                series.Overview,
                series.OfficialRating,
                series.CommunityRating,
                series.LibraryName,
                series.PeopleJson,
                series.ProviderIdsJson)));
        }

        foreach (var grouped in countsBySeriesId)
        {
            if (seenIds.Contains(grouped.Id))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(grouped.Name) ? "Untitled series" : grouped.Name;
            shows.Add((title, TvShowRow(
                grouped.Id,
                grouped.Id,
                title,
                grouped.Name,
                grouped.Count,
                null,
                null,
                null,
                null,
                null,
                null)));
        }

        foreach (var grouped in countsByName)
        {
            var title = string.IsNullOrWhiteSpace(grouped.Name) ? "Untitled series" : grouped.Name;
            shows.Add((title, TvShowRow(
                Guid.Empty,
                null,
                title,
                string.IsNullOrWhiteSpace(grouped.Name) ? null : grouped.Name,
                grouped.Count,
                null,
                null,
                null,
                null,
                null,
                null)));
        }

        var ordered = shows
            .OrderBy(show => show.Name, StringComparer.OrdinalIgnoreCase)
            .Select(show => show.Row)
            .ToList();
        return (ordered.Count, ordered.Take(CatalogPreviewLimit).ToList());
    }

    private static object TvShowRow(
        Guid id,
        Guid? seriesId,
        string name,
        string? seriesName,
        int episodeCount,
        string? overview,
        string? officialRating,
        float? communityRating,
        string? libraryName,
        string? peopleJson,
        string? providerIdsJson)
        => new
        {
            id,
            seriesId,
            seriesName = seriesName ?? name,
            name,
            kind = BaseItemKind.Series,
            episodeCount,
            grouped = true,
            plot = TruncatePlot(overview),
            rating = FormatRating(officialRating, communityRating),
            libraryName = libraryName ?? string.Empty,
            stars = JoinJsonNames(peopleJson, "Name"),
            ids = JoinJsonMap(providerIdsJson)
        };

    private async Task<(int Total, List<object> Rows)> LoadArtistCatalogAsync(
        IQueryable<MediaItem> query,
        CancellationToken cancellationToken)
    {
        var rows = await query
            .Select(item => new
            {
                item.ArtistsJson,
                item.AlbumArtistsJson,
                item.PeopleJson
            })
            .ToListAsync(cancellationToken);

        var groups = rows
            .GroupBy(
                row => PrimaryArtist(row.ArtistsJson, row.AlbumArtistsJson, row.PeopleJson),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mapped = groups.Select(group => (object)new
        {
            name = group.Name,
            artistName = group.Name,
            itemCount = group.Count,
            grouped = true
        }).ToList();

        return (mapped.Count, mapped.Take(CatalogPreviewLimit).ToList());
    }

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
                item.RuntimeTicks,
                item.ProductionYear,
                item.Path,
                item.SeriesName,
                item.LibraryName,
                item.PeopleJson,
                item.ProviderIdsJson,
                item.AspectRatio,
                item.TrueAspectRatio,
                item.Width,
                item.Height
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
            runtime = string.IsNullOrWhiteSpace(row.Runtime) ? FormatRuntime(row.RuntimeTicks) : row.Runtime,
            year = row.ProductionYear,
            row.Path,
            row.SeriesName,
            row.LibraryName,
            row.AspectRatio,
            format = VideoAspectFormat.Prefer(row.TrueAspectRatio, row.AspectRatio, row.Width, row.Height) ?? row.AspectRatio,
            chapters = "",
            rating = row.OfficialRating ?? row.CommunityRating?.ToString("0.#"),
            plot = TruncatePlot(row.Overview),
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

    private Task SaveChangesRetryingAsync(CancellationToken cancellationToken)
        => _db.SaveChangesIgnoringGoneRowsAsync(cancellationToken);

    private async Task ProbeChaptersAfterImportAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        try
        {
            _progress.Probing(0, itemIds.Count, 0);
            await _chapters.ProbeAsync(
                    itemIds,
                    missingOnly: false,
                    (done, total, found) => _progress.Probing(done, total, found),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ffprobe chapter scan after catalog sync failed");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException postgres
            && postgres.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;

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
                    group = LibraryGroup(l.CollectionType, l.Name)
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
            .Where(lib => lib.SyncEnabled && LibraryGroup(lib.CollectionType, lib.Name) == group)
            .Select(lib => Guid.TryParse(lib.ExternalId, out var id) && id != Guid.Empty ? id : lib.Id)
            .ToList();

    private static string EpisodeCode(int? season, int? episode, string? seasonName)
    {
        if (season is int seasonNumber && episode is int episodeNumber)
        {
            return $"S{seasonNumber:00}E{episodeNumber:00}";
        }

        if (episode is int onlyEpisode)
        {
            return $"E{onlyEpisode:00}";
        }

        return string.IsNullOrWhiteSpace(seasonName) ? string.Empty : seasonName.Trim();
    }

    private static string FormatRating(string? official, float? community)
    {
        if (!string.IsNullOrWhiteSpace(official))
        {
            return official;
        }

        return community.HasValue ? community.Value.ToString("0.#") : string.Empty;
    }

    private static string? FormatRuntime(long? ticks)
    {
        if (ticks is not > 0)
        {
            return null;
        }

        var time = TimeSpan.FromTicks(ticks.Value);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        }

        if (time.TotalMinutes >= 1)
        {
            return $"{(int)time.TotalMinutes}m {time.Seconds:00}s";
        }

        return $"{time.Seconds}s";
    }

    private static string TruncatePlot(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return string.Empty;
        }

        var plot = overview.Trim();
        return plot.Length > 240 ? plot[..237] + "..." : plot;
    }

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

    private const string UnknownArtistName = "Unknown artist";

    private static string PrimaryArtist(string? artistsJson, string? albumArtistsJson, string? peopleJson)
    {
        var name = FirstJsonName(artistsJson)
            ?? FirstJsonName(albumArtistsJson)
            ?? FirstJsonName(peopleJson, "Name");
        return string.IsNullOrWhiteSpace(name) ? UnknownArtistName : name;
    }

    private static string CatalogStars(string? peopleJson, string? artistsJson)
    {
        var fromArtists = JoinJsonNames(artistsJson, "Name");
        if (!string.IsNullOrWhiteSpace(fromArtists))
        {
            return fromArtists;
        }

        return JoinJsonNames(peopleJson, "Name");
    }

    private static string? FirstJsonName(string? json, string property = "Name")
    {
        if (string.IsNullOrWhiteSpace(json) || json is "[]" or "{}")
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var value = el.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
                else if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty(property, out var nameEl))
                {
                    var value = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
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

    private static IQueryable<MediaItem> WhereNewsCatalog(IQueryable<MediaItem> query)
        => query.Where(item =>
            item.Kind == BaseItemKind.Video
            || (item.CollectionType != null && (
                item.CollectionType.ToLower() == "homevideos"
                || item.CollectionType.ToLower() == "homevideo"
                || item.CollectionType.ToLower() == "homemovies"
                || item.CollectionType.ToLower() == "homemovie"))
            || (item.LibraryName != null && (
                EF.Functions.ILike(item.LibraryName, "%Past Tense News%")
                || EF.Functions.ILike(item.LibraryName, "%Past Tense%")
                || EF.Functions.ILike(item.LibraryName, "%Home Video%")
                || EF.Functions.ILike(item.LibraryName, "%Home Movie%"))));

    private static IQueryable<MediaItem> WhereNotNewsCatalog(IQueryable<MediaItem> query)
        => query.Where(item =>
            item.Kind != BaseItemKind.Video
            && (item.CollectionType == null || (
                item.CollectionType.ToLower() != "homevideos"
                && item.CollectionType.ToLower() != "homevideo"
                && item.CollectionType.ToLower() != "homemovies"
                && item.CollectionType.ToLower() != "homemovie"))
            && (item.LibraryName == null || (
                !EF.Functions.ILike(item.LibraryName, "%Past Tense News%")
                && !EF.Functions.ILike(item.LibraryName, "%Past Tense%")
                && !EF.Functions.ILike(item.LibraryName, "%Home Video%")
                && !EF.Functions.ILike(item.LibraryName, "%Home Movie%"))));

    private static string NormalizeCollectionType(string? collectionType, string? name)
    {
        if (PastTenseNewsCatalog.MatchesCollectionType(collectionType)
            || PastTenseNewsCatalog.MatchesLibraryName(name))
        {
            return string.IsNullOrWhiteSpace(collectionType) ? "homevideos" : collectionType;
        }

        return collectionType ?? "";
    }

    public static string LibraryGroup(string? collectionType)
        => LibraryGroup(collectionType, null);

    public static string LibraryGroup(string? collectionType, string? name)
    {
        if (PastTenseNewsCatalog.MatchesCollectionType(collectionType)
            || PastTenseNewsCatalog.MatchesLibraryName(name))
        {
            return "news";
        }

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
