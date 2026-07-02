using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System.Text.Json;

namespace Jellyfin.Plugin.FinTV.Services;

public class JellyfinCatalogService
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinCatalogService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public async Task<IReadOnlyList<ResolvedCandidate>> ResolveItemAsync(
        Guid itemId,
        Channel channel,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Array.Empty<ResolvedCandidate>();
        }

        if (item is Series series)
        {
            var episodes = QueryItems(channel, parentId: series.Id);
            return PickFromPool(episodes, channel, anchor);
        }

        return new[] { MapItem(item) };
    }

    public Task<IReadOnlyList<ResolvedCandidate>> ResolveCollectionAsync(
        string collectionName,
        Channel channel,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var items = QueryItems(channel, collectionName: collectionName);
        return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(PickFromPool(items, channel, anchor));
    }

    public Task<IReadOnlyList<ResolvedCandidate>> ResolveFilterAsync(
        string filterJson,
        Channel channel,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        FilterDefinition? filter = null;
        try
        {
            filter = JsonSerializer.Deserialize<FilterDefinition>(filterJson);
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(Array.Empty<ResolvedCandidate>());
        }

        var items = QueryItems(channel, filter);
        return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(PickFromPool(items, channel, anchor));
    }

    public IReadOnlyList<BaseItem> QueryItems(
        Channel channel,
        FilterDefinition? filter = null,
        string? collectionName = null,
        Guid? parentId = null)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = GetQueryItemTypes(channel),
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        if (parentId.HasValue)
        {
            query.ParentId = parentId.Value;
            query.IncludeItemTypes = new[] { BaseItemKind.Episode };
        }

        ApplyFilterToQuery(query, filter);
        MergeChannelFilter(query, channel);

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            query.Name = collectionName;
        }

        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    public IReadOnlyList<BaseItem> BrowseForAiManifest(Channel channel, ChannelCatalogMode catalogMode, int limit)
    {
        var kinds = GetManifestItemTypes(channel, catalogMode);
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = kinds,
            Limit = Math.Clamp(limit, 1, 1000),
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        MergeChannelFilter(query, channel);
        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    public int CountForAiManifest(Channel channel, ChannelCatalogMode catalogMode)
    {
        var kinds = GetManifestItemTypes(channel, catalogMode);
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = kinds
        };

        MergeChannelFilter(query, channel);
        return _libraryManager.GetItemsResult(query).TotalRecordCount;
    }

    public BaseItem? GetItemById(Guid id) => _libraryManager.GetItemById(id);

    public IReadOnlyList<BaseItem> QueryAllMusicAudio()
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    public IReadOnlyList<BaseItem> QueryMusicAudioFromLibrary(string? libraryId, string? libraryName)
    {
        var library = ResolveMusicLibrary(libraryId, libraryName);
        if (library is null)
        {
            return Array.Empty<BaseItem>();
        }

        var query = new InternalItemsQuery
        {
            ParentId = library.Id,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    public IReadOnlyList<MusicLibraryInfo> GetMusicLibraries()
    {
        return EnumerateMusicLibraries()
            .Select(folder => new MusicLibraryInfo
            {
                Id = folder.Id,
                Name = folder.Name
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CollectionFolder? ResolveMusicLibrary(string? libraryId, string? libraryName)
    {
        if (!string.IsNullOrWhiteSpace(libraryId) && Guid.TryParse(libraryId, out var parsedId))
        {
            if (_libraryManager.GetItemById(parsedId) is CollectionFolder folderById
                && folderById.CollectionType == CollectionType.music)
            {
                return folderById;
            }
        }

        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return null;
        }

        return EnumerateMusicLibraries()
            .FirstOrDefault(folder => folder.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<CollectionFolder> EnumerateMusicLibraries()
    {
        var root = _libraryManager.GetUserRootFolder();
        foreach (var child in root.Children)
        {
            if (child is CollectionFolder folder && folder.CollectionType == CollectionType.music)
            {
                yield return folder;
            }
        }
    }

    public TimeSpan GetRuntime(BaseItem item)
    {
        if (item.RunTimeTicks.HasValue)
        {
            return TimeSpan.FromTicks(item.RunTimeTicks.Value);
        }

        return TimeSpan.FromMinutes(30);
    }

    public int GetRuntimeMinutes(BaseItem item)
        => (int)Math.Max(1, Math.Round(GetRuntime(item).TotalMinutes));

    public string? GetPrimaryImagePath(BaseItem item)
    {
        return item.HasImage(ImageType.Primary)
            ? item.GetImagePath(ImageType.Primary)
            : null;
    }

    public string? GetMediaPath(BaseItem item)
    {
        return item.Path;
    }

    public static ChannelCatalogMode ResolveCatalogMode(Channel channel)
        => ChannelAiRules.ResolveCatalogMode(channel);

    private static void ApplyFilterToQuery(InternalItemsQuery query, FilterDefinition? filter)
    {
        if (filter is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            query.Genres = new[] { filter.Genre };
        }

        if (filter.Tags is { Count: > 0 })
        {
            query.Tags = filter.Tags.ToArray();
        }
    }

    private static void MergeChannelFilter(InternalItemsQuery query, Channel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.FilterJson))
        {
            return;
        }

        try
        {
            var channelFilter = JsonSerializer.Deserialize<FilterDefinition>(channel.FilterJson);
            if (channelFilter is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(channelFilter.Genre))
            {
                query.Genres = new[] { channelFilter.Genre };
            }

            if (channelFilter.Tags is { Count: > 0 })
            {
                query.Tags = channelFilter.Tags.ToArray();
            }
        }
        catch
        {
            // ignore malformed channel filter
        }
    }

    private static BaseItemKind[] GetQueryItemTypes(Channel channel)
    {
        var catalogMode = ResolveCatalogMode(channel);
        if (channel.ContentType == ChannelContentType.MusicVideo)
        {
            return new[] { BaseItemKind.MusicVideo };
        }

        if (channel.ContentType == ChannelContentType.Music)
        {
            return new[] { BaseItemKind.Audio };
        }

        return catalogMode switch
        {
            ChannelCatalogMode.MovieOnly => new[] { BaseItemKind.Movie },
            ChannelCatalogMode.Mixed => new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            _ => new[] { BaseItemKind.Episode }
        };
    }

    private static BaseItemKind[] GetManifestItemTypes(Channel channel, ChannelCatalogMode catalogMode)
    {
        if (channel.ContentType == ChannelContentType.MusicVideo)
        {
            return new[] { BaseItemKind.MusicVideo };
        }

        if (channel.ContentType == ChannelContentType.Music)
        {
            return new[] { BaseItemKind.Audio };
        }

        return catalogMode switch
        {
            ChannelCatalogMode.MovieOnly => new[] { BaseItemKind.Movie },
            ChannelCatalogMode.Mixed => new[] { BaseItemKind.Series, BaseItemKind.Movie },
            _ => new[] { BaseItemKind.Series }
        };
    }

    private static ResolvedCandidate MapItem(BaseItem item)
    {
        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : TimeSpan.FromMinutes(30);

        return new ResolvedCandidate
        {
            JellyfinItemId = item.Id,
            Title = item.Name,
            Duration = duration
        };
    }

    private static IReadOnlyList<ResolvedCandidate> PickFromPool(
        IReadOnlyList<BaseItem> items,
        Channel channel,
        PlayoutAnchorState anchor)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ResolvedCandidate>();
        }

        var catalogMode = ResolveCatalogMode(channel);
        var useEpisodeRotation = catalogMode != ChannelCatalogMode.MovieOnly
            && items.Any(i => i is Episode);

        if (useEpisodeRotation)
        {
            var grouped = items.OfType<Episode>()
                .GroupBy(e => e.SeriesId)
                .ToList();

            foreach (var group in grouped)
            {
                var key = group.Key.ToString("N");
                anchor.SeriesEpisodeIndex.TryGetValue(key, out var index);
                var ordered = group.OrderBy(e => e.ParentIndexNumber ?? 0).ThenBy(e => e.IndexNumber ?? 0).ToList();
                if (index >= ordered.Count)
                {
                    index = 0;
                }

                var episode = ordered[index];
                anchor.SeriesEpisodeIndex[key] = index + 1;
                return new[] { MapItem(episode) };
            }
        }

        return items.Select(MapItem).Take(1).ToList();
    }
}

public class FilterDefinition
{
    public string? Genre { get; set; }

    public List<string>? Tags { get; set; }

    public string? TitleContains { get; set; }
}

public class MusicLibraryInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
