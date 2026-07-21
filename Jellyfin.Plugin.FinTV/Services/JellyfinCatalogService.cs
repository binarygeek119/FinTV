using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FinTV.Configuration;
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
    private readonly HolidayChannelService _holidays;
    private readonly FinTvListService _lists;

    public JellyfinCatalogService(
        ILibraryManager libraryManager,
        HolidayChannelService holidays,
        FinTvListService lists)
    {
        _libraryManager = libraryManager;
        _holidays = holidays;
        _lists = lists;
    }

    public async Task<IReadOnlyList<ResolvedCandidate>> ResolveItemAsync(
        Guid itemId,
        Channel channel,
        PlayoutAnchorState anchor,
        DateOnly scheduleDate,
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
            var episodes = QueryItems(channel, scheduleDate: scheduleDate, parentId: series.Id);
            return PickFromPool(episodes, channel, anchor);
        }

        if (!_holidays.MatchesActiveHoliday(item, channel, scheduleDate))
        {
            return Array.Empty<ResolvedCandidate>();
        }

        return new[] { MapItem(item) };
    }

    public Task<IReadOnlyList<ResolvedCandidate>> ResolveCollectionAsync(
        string collectionName,
        Channel channel,
        PlayoutAnchorState anchor,
        DateOnly scheduleDate,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var items = QueryItems(channel, scheduleDate: scheduleDate, collectionName: collectionName);
        return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(PickFromPool(items, channel, anchor));
    }

    public Task<IReadOnlyList<ResolvedCandidate>> ResolveFilterAsync(
        string filterJson,
        Channel channel,
        PlayoutAnchorState anchor,
        DateOnly scheduleDate,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        FilterDefinition? filter = null;
        try
        {
            filter = FilterDefinition.Parse(filterJson);
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(Array.Empty<ResolvedCandidate>());
        }

        var items = QueryItems(channel, filter, scheduleDate: scheduleDate);
        return Task.FromResult<IReadOnlyList<ResolvedCandidate>>(PickFromPool(items, channel, anchor));
    }

    public async Task<IReadOnlyList<ResolvedCandidate>> ResolvePlaylistAsync(
        Guid finTvListId,
        Channel channel,
        PlayoutAnchorState anchor,
        DateOnly scheduleDate,
        int slotIndex,
        CancellationToken cancellationToken)
    {
        var list = await _lists.GetByIdAsync(finTvListId, cancellationToken);
        if (list is null)
        {
            return Array.Empty<ResolvedCandidate>();
        }

        var playlistItems = _lists.GetPlaylistItems(list.JellyfinPlaylistId);
        var items = ApplyCatalogConstraints(playlistItems, channel, scheduleDate);
        if (items.Count == 0)
        {
            return Array.Empty<ResolvedCandidate>();
        }

        if (list.PlaybackMode == ListPlaybackMode.Sequential)
        {
            anchor.ListCursor.TryGetValue(list.Id, out var index);
            if (index >= items.Count)
            {
                index = 0;
            }

            var picked = items[index];
            anchor.ListCursor[list.Id] = index + 1;
            return new[] { MapItem(picked) };
        }

        var rng = new Random(HashCode.Combine(channel.PlayoutSeed, scheduleDate.DayNumber, slotIndex, finTvListId.GetHashCode()));
        var randomItem = items[rng.Next(items.Count)];
        return new[] { MapItem(randomItem) };
    }

    public IReadOnlyList<BaseItem> QueryItems(
        Channel channel,
        FilterDefinition? filter = null,
        string? collectionName = null,
        Guid? parentId = null,
        DateOnly? scheduleDate = null)
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
        else
        {
            ApplyFilterToQuery(query, filter);
            MergeChannelFilter(query, channel);
        }

        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            query.Name = collectionName;
        }

        var requiredTags = CollectRequiredTags(channel, filter);
        var items = GetItemsWithTagFallback(
            query,
            requiredTags,
            () =>
            {
                var fallbackQuery = new InternalItemsQuery
                {
                    Recursive = query.Recursive,
                    IsVirtualItem = query.IsVirtualItem,
                    IncludeItemTypes = query.IncludeItemTypes,
                    OrderBy = query.OrderBy,
                    ParentId = query.ParentId,
                    Name = query.Name
                };

                ApplyFilterToQueryWithoutTags(fallbackQuery, filter);
                if (!parentId.HasValue)
                {
                    MergeChannelFilter(fallbackQuery, channel);
                }

                return fallbackQuery;
            });

        return ApplyFilterDefinitionConstraints(
            ApplyChannelFilterMetadata(
                ApplyCatalogConstraints(items, channel, scheduleDate),
                channel),
            filter,
            ChannelAiRules.GetYearConstraints(channel));
    }

    public IReadOnlyList<BaseItem> BrowseForAiManifest(Channel channel, ChannelCatalogMode catalogMode, int limit)
        => BrowseForAiManifestWithStats(channel, catalogMode, limit).Items;

    public AiCatalogBrowseStats BrowseForAiManifestWithStats(Channel channel, ChannelCatalogMode catalogMode, int limit)
    {
        var scheduleDate = _holidays.GetScheduleDateUtc(DateTime.UtcNow);
        var kinds = GetManifestItemTypes(channel, catalogMode);
        var requiredTags = CollectRequiredTags(channel, slotFilter: null);
        var clampedLimit = Math.Clamp(limit, 1, 1000);

        var query = CreateManifestBrowseQuery(kinds, channel, clampedLimit);
        var items = GetItemsWithTagFallback(
            query,
            requiredTags,
            () => CreateManifestBrowseQuery(kinds, channel, clampedLimit));

        var libraryItemCount = items.Count;
        var filtered = ApplyChannelFilterMetadata(
            ApplyCatalogConstraints(items, channel, scheduleDate),
            channel);

        return new AiCatalogBrowseStats
        {
            Items = filtered.Take(clampedLimit).ToList(),
            TagMatchedCount = libraryItemCount,
            AfterConstraintCount = filtered.Count
        };
    }

    public int CountForAiManifest(Channel channel, ChannelCatalogMode catalogMode)
    {
        if (!ChannelAiRules.HasCatalogConstraints(channel))
        {
            return BrowseForAiManifestWithStats(channel, catalogMode, 10000).TagMatchedCount;
        }

        return BrowseForAiManifestWithStats(channel, catalogMode, 10000).AfterConstraintCount;
    }

    public int? GetCatalogReleaseYear(BaseItem item, ChannelCatalogYearConstraints? constraints)
    {
        if (item is Series series && constraints?.UseFirstEpisodeYearForSeries == true)
        {
            return GetSeriesFirstEpisodeYear(series) ?? GetReleaseYear(series);
        }

        return GetReleaseYear(item);
    }

    public int? GetSeriesFirstEpisodeYear(Series series)
    {
        var query = new InternalItemsQuery
        {
            ParentId = series.Id,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            OrderBy = new[]
            {
                (ItemSortBy.ParentIndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
                (ItemSortBy.IndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
                (ItemSortBy.PremiereDate, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)
            },
            Limit = 1
        };

        var firstEpisode = _libraryManager.GetItemsResult(query).Items.FirstOrDefault();
        return firstEpisode is null ? GetReleaseYear(series) : GetReleaseYear(firstEpisode);
    }

    public static int? GetReleaseYear(BaseItem item)
    {
        if (item.PremiereDate.HasValue)
        {
            return item.PremiereDate.Value.Year;
        }

        return item.ProductionYear;
    }

    public bool MatchesYearConstraints(BaseItem item, ChannelCatalogYearConstraints constraints)
    {
        if (item is Episode episode)
        {
            if (!constraints.UseFirstEpisodeYearForSeries)
            {
                return false;
            }

            var series = ResolveSeriesForEpisode(episode);
            if (series is null)
            {
                return false;
            }

            var seriesYear = GetCatalogReleaseYear(series, constraints);
            if (!seriesYear.HasValue)
            {
                return true;
            }

            return constraints.ContainsYear(seriesYear);
        }

        var year = GetCatalogReleaseYear(item, constraints);
        if (!year.HasValue)
        {
            return true;
        }

        return constraints.ContainsYear(year);
    }

    public bool MatchesGenreConstraints(BaseItem item, ChannelCatalogGenreConstraints constraints)
    {
        if (item is Episode episode)
        {
            var series = ResolveSeriesForEpisode(episode);
            return series is not null && constraints.MatchesItem(series);
        }

        return constraints.MatchesItem(item);
    }

    private BaseItem? ResolveSeriesForEpisode(Episode episode)
    {
        if (episode.SeriesId == Guid.Empty)
        {
            return null;
        }

        return _libraryManager.GetItemById(episode.SeriesId);
    }

    private IReadOnlyList<BaseItem> ApplyCatalogConstraints(
        IReadOnlyList<BaseItem> items,
        Channel channel,
        DateOnly? scheduleDate = null)
    {
        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        var genreConstraints = ChannelAiRules.GetGenreConstraints(channel);
        HolidayDefinition? holiday = null;
        if (_holidays.IsHolidayChannel(channel))
        {
            var date = scheduleDate ?? _holidays.GetScheduleDateUtc(DateTime.UtcNow);
            holiday = _holidays.GetActiveHoliday(date);
        }

        if (yearConstraints is null && genreConstraints is null && holiday is null && !_holidays.IsHolidayChannel(channel))
        {
            return items;
        }

        return items.Where(item =>
        {
            if (yearConstraints is not null && !MatchesYearConstraints(item, yearConstraints))
            {
                return false;
            }

            if (genreConstraints is not null && !MatchesGenreConstraints(item, genreConstraints))
            {
                return false;
            }

            if (_holidays.IsHolidayChannel(channel))
            {
                if (holiday is null)
                {
                    return false;
                }

                return HolidayChannelCalendar.MatchesHolidayContent(item, holiday);
            }

            return true;
        }).ToList();
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

    private static void ApplyFilterToQueryWithoutTags(InternalItemsQuery query, FilterDefinition? filter)
    {
        if (filter is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(filter.Genre))
        {
            query.Genres = new[] { filter.Genre };
        }
    }

    private static IReadOnlyList<string> CollectRequiredTags(Channel channel, FilterDefinition? slotFilter)
    {
        var tags = new List<string>();
        tags.AddRange(FilterDefinition.GetOptionalJellyfinTags(channel.FilterJson));

        var catalogTag = GetChannelCatalogTag(channel);
        if (!string.IsNullOrWhiteSpace(catalogTag))
        {
            tags.Add(catalogTag);
        }

        if (slotFilter?.Tags is { Count: > 0 })
        {
            tags.AddRange(slotFilter.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)));
        }

        return tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool UseAutoTaggedCatalog()
        => Plugin.Instance?.Configuration.Ai.UseAutoTaggedCatalog == true;

    private static string? GetChannelCatalogTag(Channel channel)
    {
        if (!UseAutoTaggedCatalog())
        {
            return null;
        }

        var tag = FilterDefinition.ExtractFintvLibraryTag(channel.FilterJson);
        if (string.IsNullOrWhiteSpace(tag) || ChannelAiRules.IsExcludedFromAutoTagging(tag))
        {
            return null;
        }

        return tag;
    }

    private static bool ItemMatchesRequiredTags(BaseItem item, IReadOnlyList<string> requiredTags)
    {
        if (requiredTags.Count == 0)
        {
            return true;
        }

        var itemTags = item.Tags?.ToList();
        if (itemTags is null || itemTags.Count == 0)
        {
            return false;
        }

        return requiredTags.All(required =>
            itemTags.Any(tag => tag.Equals(required, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<BaseItem> FilterByRequiredTags(
        IReadOnlyList<BaseItem> items,
        IReadOnlyList<string> requiredTags)
    {
        if (requiredTags.Count == 0)
        {
            return items;
        }

        return items.Where(item => ItemMatchesRequiredTags(item, requiredTags)).ToList();
    }

    private IReadOnlyList<BaseItem> GetItemsWithTagFallback(
        InternalItemsQuery query,
        IReadOnlyList<string> requiredTags,
        Func<InternalItemsQuery> createFallbackQuery)
    {
        var items = FilterByRequiredTags(_libraryManager.GetItemsResult(query).Items.ToList(), requiredTags);
        if (requiredTags.Count > 0 && items.Count == 0 && query.Tags is { Length: > 0 })
        {
            var fallbackQuery = createFallbackQuery();
            items = FilterByRequiredTags(
                _libraryManager.GetItemsResult(fallbackQuery).Items.ToList(),
                requiredTags);
        }

        return items;
    }

    private InternalItemsQuery CreateManifestBrowseQuery(
        BaseItemKind[] kinds,
        Channel channel,
        int limit)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = kinds,
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        var catalogTag = GetChannelCatalogTag(channel);
        if (!string.IsNullOrWhiteSpace(catalogTag))
        {
            query.Tags = new[] { catalogTag };
            query.Limit = Math.Clamp(limit * 5, limit, 5000);
        }
        else if (!ChannelAiRules.HasCatalogConstraints(channel))
        {
            query.Limit = limit;
        }

        MergeChannelFilter(query, channel);
        return query;
    }

    private IReadOnlyList<BaseItem> ApplyChannelFilterMetadata(
        IReadOnlyList<BaseItem> items,
        Channel channel)
    {
        var filter = FilterDefinition.Parse(channel.FilterJson);
        if (filter is null)
        {
            return items;
        }

        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        if (string.IsNullOrWhiteSpace(filter.TitleContains)
            && string.IsNullOrWhiteSpace(filter.MinRating)
            && string.IsNullOrWhiteSpace(filter.MaxRating)
            && (yearConstraints is not null || (!filter.MinYear.HasValue && !filter.MaxYear.HasValue)))
        {
            return items;
        }

        return ApplyFilterDefinitionConstraints(items, filter, yearConstraints);
    }

    private IReadOnlyList<BaseItem> ApplyFilterDefinitionConstraints(
        IReadOnlyList<BaseItem> items,
        FilterDefinition? filter,
        ChannelCatalogYearConstraints? yearConstraints = null)
    {
        if (filter is null)
        {
            return items;
        }

        return items.Where(item =>
        {
            if (!string.IsNullOrWhiteSpace(filter.TitleContains)
                && !item.Name.Contains(filter.TitleContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (yearConstraints is null)
            {
                var year = GetReleaseYear(item);
                if (filter.MinYear.HasValue && (!year.HasValue || year.Value < filter.MinYear.Value))
                {
                    return false;
                }

                if (filter.MaxYear.HasValue && (!year.HasValue || year.Value > filter.MaxYear.Value))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(filter.MinRating)
                && !RatingAtLeast(item.OfficialRating, filter.MinRating))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(filter.MaxRating)
                && !RatingAtMost(item.OfficialRating, filter.MaxRating))
            {
                return false;
            }

            return true;
        }).ToList();
    }

    private static bool RatingAtLeast(string? itemRating, string minRating)
    {
        var itemScore = ParseRatingScore(itemRating);
        var minScore = ParseRatingScore(minRating);
        return itemScore.HasValue && minScore.HasValue && itemScore.Value >= minScore.Value;
    }

    private static bool RatingAtMost(string? itemRating, string maxRating)
    {
        var itemScore = ParseRatingScore(itemRating);
        var maxScore = ParseRatingScore(maxRating);
        if (!maxScore.HasValue)
        {
            return true;
        }

        if (!itemScore.HasValue)
        {
            return true;
        }

        return itemScore.Value <= maxScore.Value;
    }

    private static int? ParseRatingScore(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        return rating.ToUpperInvariant() switch
        {
            "G" => 1,
            "PG" => 2,
            "PG-13" => 3,
            "TV-Y" => 1,
            "TV-Y7" => 2,
            "TV-G" => 2,
            "TV-PG" => 3,
            "R" => 4,
            "TV-14" => 4,
            "NC-17" => 5,
            "TV-MA" => 5,
            _ => null
        };
    }

    private void MergeChannelFilter(InternalItemsQuery query, Channel channel)
    {
        if (TryApplyLibraryScope(query, channel))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(channel.FilterJson))
        {
            return;
        }

        var channelFilter = FilterDefinition.Parse(channel.FilterJson);
        if (channelFilter is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(channelFilter.Genre))
        {
            query.Genres = new[] { channelFilter.Genre };
        }

        var catalogTag = GetChannelCatalogTag(channel);
        if (!string.IsNullOrWhiteSpace(catalogTag))
        {
            query.Tags = new[] { catalogTag };
        }
    }

    private bool TryApplyLibraryScope(InternalItemsQuery query, Channel channel)
    {
        var libraryConstraint = ChannelAiRules.GetLibraryConstraints(channel);
        if (libraryConstraint is null)
        {
            return false;
        }

        var folder = ResolveLibraryFolder(libraryConstraint.LibraryName);
        if (folder is null)
        {
            return false;
        }

        query.ParentId = folder.Id;
        query.Tags = Array.Empty<string>();
        query.Genres = Array.Empty<string>();
        return true;
    }

    private CollectionFolder? ResolveLibraryFolder(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return null;
        }

        var root = _libraryManager.GetUserRootFolder();
        foreach (var child in root.Children)
        {
            if (child is CollectionFolder folder
                && folder.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
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
            ChannelCatalogMode.MusicVideoOnly => new[] { BaseItemKind.MusicVideo },
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
            ChannelCatalogMode.MusicVideoOnly => new[] { BaseItemKind.MusicVideo },
            _ => new[] { BaseItemKind.Series }
        };
    }

    private ResolvedCandidate MapItem(BaseItem item)
    {
        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : TimeSpan.FromMinutes(30);

        return new ResolvedCandidate
        {
            JellyfinItemId = item.Id,
            Title = BuildPlayoutTitle(item),
            Duration = duration
        };
    }

    private string BuildPlayoutTitle(BaseItem item)
    {
        if (item is Episode episode)
        {
            var series = ResolveSeriesForEpisode(episode);
            var onScreen = GuideMetadataService.FormatOnScreen(episode.ParentIndexNumber, episode.IndexNumber);
            if (series is not null && !string.IsNullOrWhiteSpace(onScreen))
            {
                return $"{series.Name} · {onScreen} · {episode.Name}";
            }

            if (series is not null)
            {
                return $"{series.Name} · {episode.Name}";
            }
        }

        return item.Name;
    }

    private IReadOnlyList<ResolvedCandidate> PickFromPool(
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

        return items.Select(item => MapItem(item)).Take(1).ToList();
    }
}

public class AiCatalogBrowseStats
{
    public IReadOnlyList<BaseItem> Items { get; init; } = Array.Empty<BaseItem>();

    public int TagMatchedCount { get; init; }

    public int AfterConstraintCount { get; init; }
}

public class MusicLibraryInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
