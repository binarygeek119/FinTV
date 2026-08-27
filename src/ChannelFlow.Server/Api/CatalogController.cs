using FinTv;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

/// <summary>
/// Jellyfin library search helpers for the ChannelFlow admin UI.
/// </summary>
[ApiController]
[Route("api/catalog")]
[Authorize(Policy = "admin")]
public class CatalogController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinCatalogService _catalog;
    private readonly FinTvDbContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="catalog">ChannelFlow catalog service.</param>
    /// <param name="db">Database context.</param>
    public CatalogController(ILibraryManager libraryManager, JellyfinCatalogService catalog, FinTvDbContext db)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
        _db = db;
    }

    /// <summary>
    /// Searches Jellyfin library items for lineup slot assignment.
    /// </summary>
    /// <param name="q">Search text.</param>
    /// <param name="contentType">Optional channel content type filter.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library items.</returns>
    [HttpGet("search")]
    public ActionResult<IEnumerable<object>> Search(
        [FromQuery] string q,
        [FromQuery] ChannelContentType? contentType,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return Ok(Array.Empty<object>());
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            SearchTerm = q.Trim(),
            Limit = Math.Clamp(limit, 1, 50),
            IncludeItemTypes = contentType.HasValue
                ? GetItemTypes(contentType.Value)
                : new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Audio },
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        var items = _libraryManager.GetItemsResult(query).Items;
        return Ok(items.Select(MapSearchResult));
    }

    /// <summary>
    /// Resolves display metadata for Jellyfin item identifiers.
    /// </summary>
    /// <param name="request">Lookup request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved item metadata.</returns>
    [HttpPost("lookup")]
    public ActionResult<IEnumerable<object>> Lookup([FromBody] CatalogLookupRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Ids is not { Count: > 0 })
        {
            return Ok(Array.Empty<object>());
        }

        var results = new List<object>();
        foreach (var id in request.Ids.Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null)
            {
                results.Add(MapSearchResult(item));
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Browses library items by tag for AI lineup generation.
    /// </summary>
    /// <param name="tag">Library tag filter.</param>
    /// <param name="contentType">Optional channel content type.</param>
    /// <param name="catalogMode">Optional catalog mode override.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library items.</returns>
    [HttpGet("browse")]
    public ActionResult<object> Browse(
        [FromQuery] string? tag,
        [FromQuery] ChannelContentType? contentType,
        [FromQuery] ChannelCatalogMode? catalogMode,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = new Channel
        {
            ContentType = contentType ?? ChannelContentType.TvShow,
            FilterJson = string.IsNullOrWhiteSpace(tag)
                ? null
                : FinTvJson.Serialize(new { tags = new[] { tag } }),
            CatalogMode = catalogMode
        };

        var mode = JellyfinCatalogService.ResolveCatalogMode(channel);
        var items = _catalog.BrowseForAiManifest(channel, mode, Math.Clamp(limit, 1, 500));
        return Ok(new
        {
            catalogMode = mode.ToString(),
            total = items.Count,
            items = items.Select(MapSearchResult)
        });
    }

    /// <summary>
    /// Lists Jellyfin libraries from the synced catalog and the current sync selection.
    /// </summary>
    [HttpGet("libraries")]
    public async Task<ActionResult<object>> GetLibraries(CancellationToken cancellationToken)
    {
        var settings = FinTvRuntime.Current?.Configuration.JellyfinLibraries ?? new JellyfinLibrarySettings();
        var libraries = await ListSyncedLibrariesAsync(cancellationToken);
        return Ok(new
        {
            libraries,
            tvLibraryIds = settings.TvLibraryIds,
            movieLibraryIds = settings.MovieLibraryIds,
            musicLibraryIds = settings.MusicLibraryIds,
            musicVideoLibraryIds = settings.MusicVideoLibraryIds,
            homeVideoLibraryIds = settings.HomeVideoLibraryIds
        });
    }

    /// <summary>
    /// Lists synced catalog rows grouped for the Jellyfin Library tables.
    /// </summary>
    [HttpGet("media")]
    public async Task<ActionResult<object>> GetMedia(CancellationToken cancellationToken)
    {
        var rows = await _db.MediaItems.AsNoTracking()
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
                item.CollectionType,
                item.Album,
                item.PeopleJson,
                item.ProviderIdsJson,
                item.ArtistsJson,
                item.Width,
                item.Height,
                item.AspectRatio,
                item.TrueAspectRatio,
                ChapterCount = item.Chapters.Count
            })
            .ToListAsync(cancellationToken);

        var tvShows = new List<object>();
        var movies = new List<object>();
        var music = new List<object>();
        var musicVideos = new List<object>();
        var pastTenseNews = new List<object>();

        foreach (var row in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (row.Kind is BaseItemKind.Folder or BaseItemKind.Playlist or BaseItemKind.Season)
            {
                continue;
            }

            var mapped = MapMediaRow(row.Id, row.Name, row.Kind, row.Overview, row.OfficialRating, row.CommunityRating,
                row.Runtime, row.RuntimeTicks, row.Path, row.SeriesName, row.SeasonName, row.IndexNumber,
                row.ParentIndexNumber, row.LibraryName, row.Album, row.PeopleJson, row.ProviderIdsJson,
                row.ArtistsJson, VideoAspectFormat.Prefer(row.TrueAspectRatio, row.AspectRatio, row.Width, row.Height), row.ChapterCount);

            if (IsNewsItem(row.Kind, row.CollectionType, row.LibraryName))
            {
                pastTenseNews.Add(mapped);
            }
            else if (row.Kind is BaseItemKind.Episode or BaseItemKind.Series)
            {
                tvShows.Add(mapped);
            }
            else if (row.Kind == BaseItemKind.Movie)
            {
                movies.Add(mapped);
            }
            else if (row.Kind == BaseItemKind.Audio)
            {
                music.Add(mapped);
            }
            else if (row.Kind == BaseItemKind.MusicVideo)
            {
                musicVideos.Add(mapped);
            }
        }

        return Ok(new
        {
            tvShows,
            movies,
            music,
            musicVideos,
            pastTenseNews
        });
    }

    /// <summary>
    /// Saves which Jellyfin libraries ChannelFlow should use for TV, movies, music, and music videos.
    /// </summary>
    [HttpPut("libraries")]
    public IActionResult UpdateLibraries([FromBody] JellyfinLibrarySettingsRequest? request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.JellyfinLibraries = new JellyfinLibrarySettings
        {
            TvLibraryIds = JellyfinLibrarySettings.Normalize(request?.TvLibraryIds),
            MovieLibraryIds = JellyfinLibrarySettings.Normalize(request?.MovieLibraryIds),
            MusicLibraryIds = JellyfinLibrarySettings.Normalize(request?.MusicLibraryIds),
            MusicVideoLibraryIds = JellyfinLibrarySettings.Normalize(request?.MusicVideoLibraryIds),
            HomeVideoLibraryIds = JellyfinLibrarySettings.Normalize(request?.HomeVideoLibraryIds),
            Libraries = plugin.Configuration.JellyfinLibraries.Libraries
        };
        plugin.SaveConfiguration();
        return Ok(plugin.Configuration.JellyfinLibraries);
    }

    private async Task<List<object>> ListSyncedLibrariesAsync(CancellationToken cancellationToken)
    {
        var reported = FinTvRuntime.Current?.Configuration.JellyfinLibraries.Libraries;
        if (reported is { Count: > 0 })
        {
            return await ListReportedLibrariesAsync(reported, cancellationToken);
        }
        var rows = await _db.MediaItems.AsNoTracking()
            .Select(item => new LibraryScanRow(
                item.Id,
                item.Name,
                item.Kind,
                item.ParentId,
                item.SeriesId,
                item.LibraryId,
                item.LibraryName,
                item.CollectionType))
            .ToListAsync(cancellationToken);

        var itemsById = rows.ToDictionary(row => row.Id);
        var libraries = new Dictionary<Guid, LibraryListRow>();

        foreach (var folder in rows.Where(row => IsJellyfinLibraryFolder(row)))
        {
            var library = GetOrAddLibrary(libraries, folder.Id, folder.Name, folder.CollectionType);
            library.MemberIds.Add(folder.Id);
        }

        foreach (var row in rows)
        {
            if (row.Kind is BaseItemKind.Folder or BaseItemKind.Playlist)
            {
                continue;
            }

            var resolvedId = ResolveLibraryId(row, itemsById);
            if (resolvedId is null)
            {
                continue;
            }

            itemsById.TryGetValue(resolvedId.Value, out var folder);
            var name = FirstRealName(folder?.Name, row.LibraryName);
            var collectionType = FirstRealName(folder?.CollectionType, row.CollectionType);
            var library = GetOrAddLibrary(libraries, resolvedId.Value, name, collectionType);
            library.ItemCount++;
            library.Kinds.Add(row.Kind);
            if (row.LibraryId is Guid storedId && storedId != Guid.Empty)
            {
                library.MemberIds.Add(storedId);
            }

            library.MemberIds.Add(resolvedId.Value);
        }

        foreach (var library in libraries.Values)
        {
            if (string.IsNullOrWhiteSpace(library.CollectionType))
            {
                library.CollectionType = InferCollectionType(library.Kinds);
            }

            library.MemberIds.Add(library.Id);
        }

        return libraries.Values
            .Select(library =>
            {
                var groups = LibraryGroupsFor(library.CollectionType, library.Kinds);
                return new { library, groups };
            })
            .Where(row => row.groups.Length > 0 && !IsPlaceholderName(row.library.Name))
            .OrderBy(row => row.library.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => (object)new
            {
                id = row.library.Id,
                ids = row.library.MemberIds.OrderBy(id => id).ToArray(),
                name = row.library.Name,
                collectionType = row.library.CollectionType,
                groups = row.groups,
                itemCount = row.library.ItemCount
            })
            .ToList();
    }

    private async Task<List<object>> ListReportedLibrariesAsync(
        IReadOnlyList<JellyfinLibraryInfo> reported,
        CancellationToken cancellationToken)
    {
        var counts = await _db.MediaItems.AsNoTracking()
            .Where(item => item.LibraryId != null
                && item.Kind != BaseItemKind.Folder
                && item.Kind != BaseItemKind.Playlist)
            .GroupBy(item => item.LibraryId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var countById = counts.ToDictionary(row => row.Id, row => row.Count);

        return reported
            .Where(library => library.Id != Guid.Empty && !IsPlaceholderName(library.Name))
            .Select(library =>
            {
                var groups = LibraryGroupsFor(library.CollectionType, []);
                countById.TryGetValue(library.Id, out var itemCount);
                return new
                {
                    library,
                    groups,
                    itemCount
                };
            })
            .Where(row => row.groups.Length > 0)
            .OrderBy(row => row.library.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => (object)new
            {
                id = row.library.Id,
                ids = new[] { row.library.Id },
                name = row.library.Name,
                collectionType = row.library.CollectionType,
                groups = row.groups,
                itemCount = row.itemCount
            })
            .ToList();
    }

    private static LibraryListRow GetOrAddLibrary(
        Dictionary<Guid, LibraryListRow> byId,
        Guid id,
        string? name,
        string? collectionType)
    {
        if (!byId.TryGetValue(id, out var library))
        {
            library = new LibraryListRow { Id = id };
            byId[id] = library;
        }

        ApplyLibraryName(library, name);
        ApplyCollectionType(library, collectionType);
        return library;
    }

    private static void ApplyLibraryName(LibraryListRow library, string? name)
    {
        if (IsPlaceholderName(library.Name) && !IsPlaceholderName(name))
        {
            library.Name = name!.Trim();
        }
    }

    private static void ApplyCollectionType(LibraryListRow library, string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(library.CollectionType) && !string.IsNullOrWhiteSpace(collectionType))
        {
            library.CollectionType = collectionType.Trim();
        }
    }

    private static bool IsPlaceholderName(string? name)
        => string.IsNullOrWhiteSpace(name)
            || name.Equals("Library", StringComparison.OrdinalIgnoreCase);

    private static string? FirstRealName(params string?[] values)
        => values.FirstOrDefault(value => !IsPlaceholderName(value))?.Trim();

    private static bool IsJellyfinLibraryFolder(LibraryScanRow row)
    {
        if (row.Kind != BaseItemKind.Folder || IsPlaceholderName(row.Name))
        {
            return false;
        }

        return IsKnownLibraryType(row.CollectionType)
            || row.ParentId is null
            || row.ParentId == Guid.Empty;
    }

    private static Guid? ResolveLibraryId(LibraryScanRow row, IReadOnlyDictionary<Guid, LibraryScanRow> itemsById)
    {
        return WalkToLibraryFolder(row.LibraryId, itemsById)
            ?? WalkToLibraryFolder(row.ParentId, itemsById)
            ?? WalkToLibraryFolder(row.SeriesId, itemsById)
            ?? (row.LibraryId is Guid libraryId && libraryId != Guid.Empty ? libraryId : null);
    }

    private static Guid? WalkToLibraryFolder(Guid? start, IReadOnlyDictionary<Guid, LibraryScanRow> itemsById)
    {
        var current = start;
        Guid? lastFolder = null;
        var seen = new HashSet<Guid>();
        while (current is Guid id && id != Guid.Empty && seen.Add(id))
        {
            if (!itemsById.TryGetValue(id, out var node))
            {
                break;
            }

            if (node.Kind == BaseItemKind.Folder)
            {
                lastFolder = node.Id;
                if (IsJellyfinLibraryFolder(node))
                {
                    return node.Id;
                }
            }

            current = node.ParentId is Guid parent && parent != Guid.Empty
                ? parent
                : node.SeriesId;
        }

        return lastFolder;
    }

    private static bool IsKnownLibraryType(string? collectionType)
        => LibraryGroupForType(collectionType) is not null;

    private static string? LibraryGroupForType(string? collectionType)
    {
        var type = (collectionType ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty);
        return type switch
        {
            "tvshows" or "tvshow" or "tv" or "series" or "shows" => "tv",
            "movies" or "movie" => "movies",
            "music" or "audio" => "music",
            "musicvideos" or "musicvideo" => "musicvideos",
            "homevideos" or "homevideo" or "homemovies" or "homemovie" or "news" => "news",
            _ => null
        };
    }

    private static string[] LibraryGroupsFor(string? collectionType, HashSet<BaseItemKind> kinds)
    {
        var fromType = LibraryGroupForType(collectionType);
        if (fromType is not null)
        {
            return [fromType];
        }

        var groups = new List<string>();
        if (kinds.Any(kind => kind is BaseItemKind.Series or BaseItemKind.Episode or BaseItemKind.Season))
        {
            groups.Add("tv");
        }

        if (kinds.Contains(BaseItemKind.Movie))
        {
            groups.Add("movies");
        }

        if (kinds.Contains(BaseItemKind.Audio))
        {
            groups.Add("music");
        }

        if (kinds.Contains(BaseItemKind.MusicVideo))
        {
            groups.Add("musicvideos");
        }

        if (kinds.Contains(BaseItemKind.Video))
        {
            groups.Add("news");
        }

        return groups.ToArray();
    }

    private static string? InferCollectionType(HashSet<BaseItemKind> kinds)
    {
        var groups = LibraryGroupsFor(null, kinds);
        return groups.Length switch
        {
            1 when groups[0] == "tv" => "tvshows",
            1 when groups[0] == "movies" => "movies",
            1 when groups[0] == "music" => "music",
            1 when groups[0] == "musicvideos" => "musicvideos",
            1 when groups[0] == "news" => "homevideos",
            _ => null
        };
    }

    private static bool IsNewsItem(BaseItemKind kind, string? collectionType, string? libraryName)
    {
        if (kind == BaseItemKind.Video)
        {
            return true;
        }

        if (LibraryGroupForType(collectionType) == "news")
        {
            return true;
        }

        var name = libraryName ?? string.Empty;
        return PastTenseNewsCatalog.MatchesLibraryName(name)
            || PastTenseNewsCatalog.MatchesCollectionType(collectionType)
            || name.Contains("news", StringComparison.OrdinalIgnoreCase);
    }

    private static object MapMediaRow(
        Guid id,
        string? name,
        BaseItemKind kind,
        string? overview,
        string? officialRating,
        float? communityRating,
        string? runtime,
        long? runtimeTicks,
        string? path,
        string? seriesName,
        string? seasonName,
        int? indexNumber,
        int? parentIndexNumber,
        string? libraryName,
        string? album,
        string? peopleJson,
        string? providerIdsJson,
        string? artistsJson,
        string? aspectRatio,
        int chapterCount)
    {
        var stars = ReadStars(peopleJson, artistsJson);
        var ids = FormatIds(id, providerIdsJson);
        var plot = string.IsNullOrWhiteSpace(overview)
            ? string.Empty
            : overview.Trim();
        if (plot.Length > 240)
        {
            plot = plot[..237] + "...";
        }

        var title = name ?? string.Empty;
        if (kind == BaseItemKind.Episode && !string.IsNullOrWhiteSpace(seriesName))
        {
            var episode = parentIndexNumber.HasValue && indexNumber.HasValue
                ? $"S{parentIndexNumber.Value:00}E{indexNumber.Value:00}"
                : seasonName;
            title = string.IsNullOrWhiteSpace(episode)
                ? $"{seriesName} · {title}"
                : $"{seriesName} · {episode} · {title}";
        }
        else if (!string.IsNullOrWhiteSpace(album) && kind == BaseItemKind.Audio)
        {
            title = $"{name} ({album})";
        }

        return new
        {
            id,
            name = title,
            runtime = string.IsNullOrWhiteSpace(runtime) ? FormatRuntime(runtimeTicks) : runtime,
            format = aspectRatio ?? string.Empty,
            chapters = chapterCount == 0 ? string.Empty : $"{chapterCount}",
            rating = FormatRating(officialRating, communityRating),
            plot,
            stars,
            path = path ?? string.Empty,
            ids,
            libraryName = libraryName ?? string.Empty
        };
    }

    private static string ReadStars(string? peopleJson, string? artistsJson)
    {
        var names = new List<string>();
        try
        {
            var people = JsonSerializer.Deserialize<List<CatalogPersonDto>>(peopleJson ?? "[]") ?? [];
            names.AddRange(people
                .Where(person =>
                    string.Equals(person.Type, "Actor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(person.Type, "GuestStar", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(person.Type))
                .Select(person => person.Name)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>());
        }
        catch
        {
            // ignore malformed people json from older syncs
        }

        if (names.Count == 0)
        {
            try
            {
                names.AddRange(JsonSerializer.Deserialize<List<string>>(artistsJson ?? "[]") ?? []);
            }
            catch
            {
                // ignore malformed artists json
            }
        }

        return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
    }

    private static string FormatIds(Guid id, string? providerIdsJson)
    {
        var parts = new List<string> { id.ToString("N") };
        try
        {
            var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(providerIdsJson ?? "{}")
                ?? new Dictionary<string, string>();
            foreach (var pair in ids.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Take(4))
            {
                parts.Add($"{pair.Key} {pair.Value}");
            }
        }
        catch
        {
            // ignore malformed provider id json
        }

        return string.Join(" · ", parts);
    }

    private static string FormatRating(string? official, float? community)
    {
        official = AiCatalogManifestBuilder.NormalizeOfficialRating(official);
        if (!string.IsNullOrWhiteSpace(official) && community.HasValue)
        {
            return $"{official} · {community.Value:0.0}";
        }

        if (!string.IsNullOrWhiteSpace(official))
        {
            return official!;
        }

        return community.HasValue ? community.Value.ToString("0.0") : string.Empty;
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

    private static object MapSearchResult(BaseItem item)
    {
        var runtime = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : (TimeSpan?)null;

        return new
        {
            id = item.Id,
            name = item.Name,
            type = item.GetBaseItemKind().ToString(),
            runtimeMinutes = runtime.HasValue ? (int)Math.Round(runtime.Value.TotalMinutes) : (int?)null,
            year = item.ProductionYear
        };
    }

    private static BaseItemKind[] GetItemTypes(ChannelContentType contentType)
    {
        return contentType switch
        {
            ChannelContentType.TvShow => new[] { BaseItemKind.Episode },
            ChannelContentType.Movie => new[] { BaseItemKind.Movie },
            ChannelContentType.MusicVideo => new[] { BaseItemKind.MusicVideo },
            ChannelContentType.Music => new[] { BaseItemKind.Audio },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Audio }
        };
    }

    private sealed class LibraryListRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? CollectionType { get; set; }

        public int ItemCount { get; set; }

        public HashSet<BaseItemKind> Kinds { get; } = new();

        public HashSet<Guid> MemberIds { get; } = new();
    }

    private sealed record LibraryScanRow(
        Guid Id,
        string? Name,
        BaseItemKind Kind,
        Guid? ParentId,
        Guid? SeriesId,
        Guid? LibraryId,
        string? LibraryName,
        string? CollectionType);
}

/// <summary>
/// Request body for catalog item lookup.
/// </summary>
public class CatalogLookupRequest
{
    /// <summary>
    /// Gets or sets Jellyfin item identifiers to resolve.
    /// </summary>
    public List<Guid> Ids { get; set; } = new();
}

/// <summary>
/// Selected Jellyfin libraries for each catalog type.
/// </summary>
public class JellyfinLibrarySettingsRequest
{
    public List<Guid>? TvLibraryIds { get; set; }

    public List<Guid>? MovieLibraryIds { get; set; }

    public List<Guid>? MusicLibraryIds { get; set; }

    public List<Guid>? MusicVideoLibraryIds { get; set; }

    public List<Guid>? HomeVideoLibraryIds { get; set; }
}
