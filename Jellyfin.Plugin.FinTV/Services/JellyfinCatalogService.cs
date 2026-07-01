using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Entities;
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
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Array.Empty<ResolvedCandidate>();
        }

        return new[] { MapItem(item) };
    }

    public Task<IReadOnlyList<ResolvedCandidate>> ResolveCollectionAsync(
        string collectionName,
        Channel channel,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
        var items = QueryItems(channel, collectionName: collectionName);
        return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(PickFromPool(items, channel, anchor));
    }

    public Task<IReadOnlyList<ResolvedCandidate>> ResolveFilterAsync(
        string filterJson,
        Channel channel,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
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

    public IReadOnlyList<BaseItem> QueryItems(Channel channel, FilterDefinition? filter = null, string? collectionName = null)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = GetItemTypes(channel.ContentType),
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        if (!string.IsNullOrWhiteSpace(filter?.Genre))
        {
            query.Genres = new[] { filter.Genre };
        }

        if (filter?.Tags is { Count: > 0 })
        {
            query.Tags = filter.Tags.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            query.Name = collectionName;
        }

        if (!string.IsNullOrWhiteSpace(channel.FilterJson))
        {
            try
            {
                var channelFilter = JsonSerializer.Deserialize<FilterDefinition>(channel.FilterJson);
                if (channelFilter?.Genre is not null)
                {
                    query.Genres = new[] { channelFilter.Genre };
                }
            }
            catch
            {
                // ignore malformed channel filter
            }
        }

        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    public TimeSpan GetRuntime(BaseItem item)
    {
        if (item.RunTimeTicks.HasValue)
        {
            return TimeSpan.FromTicks(item.RunTimeTicks.Value);
        }

        return TimeSpan.FromMinutes(30);
    }

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

    private static BaseItemKind[] GetItemTypes(ChannelContentType contentType)
    {
        return contentType switch
        {
            ChannelContentType.TvShow => new[] { BaseItemKind.Episode },
            ChannelContentType.Movie => new[] { BaseItemKind.Movie },
            ChannelContentType.MusicVideo => new[] { BaseItemKind.MusicVideo },
            ChannelContentType.Music => new[] { BaseItemKind.Audio },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Episode }
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

        if (channel.ContentType == ChannelContentType.TvShow)
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
