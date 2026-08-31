using System.Collections.Concurrent;
using System.Text.Json;
using FinTv;
using FinTv.Configuration;
using FinTv.Domain;

namespace FinTv.Services;

public class JellyfinCatalogService
{
    private static readonly TimeSpan MusicAudioCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (DateTime Utc, IReadOnlyList<BaseItem> Tracks)> MusicAudioCache = new(StringComparer.Ordinal);

    private readonly Dictionary<string, IReadOnlyList<BaseItem>> _queryCache = new(StringComparer.Ordinal);
    private readonly ILibraryManager _libraryManager;
    private readonly HolidayChannelService _holidays;
    private readonly FinTvListService _lists;
    private readonly MusicVideoChannelListService _musicVideoLists;

    public JellyfinCatalogService(
        ILibraryManager libraryManager,
        HolidayChannelService holidays,
        FinTvListService lists,
        MusicVideoChannelListService musicVideoLists)
    {
        _libraryManager = libraryManager;
        _holidays = holidays;
        _lists = lists;
        _musicVideoLists = musicVideoLists;
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

    public bool IsPlayableOnChannel(BaseItem item, Channel channel, DateOnly scheduleDate)
    {
        var kinds = GetQueryItemTypes(channel);
        if (!kinds.Contains(item.Kind))
        {
            return false;
        }

        return ApplyChannelFilterMetadata(
            ApplyCatalogConstraints([item], channel, scheduleDate),
            channel).Count > 0;
    }

    public IReadOnlyList<BaseItem> QueryItems(
        Channel channel,
        FilterDefinition? filter = null,
        string? collectionName = null,
        Guid? parentId = null,
        DateOnly? scheduleDate = null)
    {
        var cacheKey = $"{channel.Id:N}|{scheduleDate?.DayNumber.ToString() ?? ""}|{parentId?.ToString("N") ?? ""}|{collectionName ?? ""}|{JsonSerializer.Serialize(filter)}";
        if (_queryCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = GetQueryItemTypes(channel),
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
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

        // Episode rows rarely copy series tags. Once we already chose a series, play its episodes.
        var requiredTags = parentId.HasValue
            ? Array.Empty<string>()
            : CollectRequiredTags(channel, filter);
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

        var result = ApplyMusicVideoArtistFilter(
            ApplyFilterDefinitionConstraints(
                ApplyChannelFilterMetadata(
                    ApplyCatalogConstraints(items, channel, scheduleDate),
                    channel),
                filter,
                ChannelAiRules.GetYearConstraints(channel)),
            channel);
        _queryCache[cacheKey] = result;
        return result;
    }

    public IReadOnlyList<ResolvedCandidate> ListResolvedMusicVideos(Channel channel, DateOnly? scheduleDate = null)
        => QueryItems(channel, scheduleDate: scheduleDate).Select(MapItem).ToList();

    public IReadOnlyList<BaseItem> BrowseForAiManifest(Channel channel, ChannelCatalogMode catalogMode, int limit)
        => BrowseForAiManifestWithStats(channel, catalogMode, limit).Items;

    public AiCatalogBrowseStats BrowseForAiManifestWithStats(Channel channel, ChannelCatalogMode catalogMode, int limit)
    {
        var scheduleDate = _holidays.GetScheduleDateUtc(DateTime.UtcNow);
        var requiredTags = CollectRequiredTags(channel, slotFilter: null);
        var clampedLimit = Math.Clamp(limit, 1, 1000);

        if (catalogMode == ChannelCatalogMode.Mixed
            && !PastTenseNewsCatalog.IsPastTenseNewsChannel(channel)
            && channel.ContentType is not ChannelContentType.Music and not ChannelContentType.MusicVideo)
        {
            var series = BrowseManifestKind(channel, [BaseItemKind.Series], requiredTags, scheduleDate, clampedLimit);
            var movies = BrowseManifestKind(channel, [BaseItemKind.Movie], requiredTags, scheduleDate, clampedLimit);
            return new AiCatalogBrowseStats
            {
                Items = TakeBalancedMixedCatalog(series, movies, channel.ContentType, clampedLimit),
                TagMatchedCount = series.Count + movies.Count,
                AfterConstraintCount = series.Count + movies.Count
            };
        }

        var kinds = GetManifestItemTypes(channel, catalogMode);
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

    private IReadOnlyList<BaseItem> BrowseManifestKind(
        Channel channel,
        BaseItemKind[] kinds,
        IReadOnlyList<string> requiredTags,
        DateOnly scheduleDate,
        int cap)
    {
        var query = CreateManifestBrowseQuery(kinds, channel, cap);
        var items = GetItemsWithTagFallback(
            query,
            requiredTags,
            () => CreateManifestBrowseQuery(kinds, channel, cap));
        return ApplyChannelFilterMetadata(
            ApplyCatalogConstraints(items, channel, scheduleDate),
            channel);
    }

    private static IReadOnlyList<BaseItem> TakeBalancedMixedCatalog(
        IReadOnlyList<BaseItem> series,
        IReadOnlyList<BaseItem> movies,
        ChannelContentType contentType,
        int limit)
    {
        var preferShows = contentType != ChannelContentType.Movie;
        var primary = preferShows ? series : movies;
        var secondary = preferShows ? movies : series;
        var primaryTake = primary.Count == 0
            ? 0
            : Math.Min(primary.Count, Math.Max(1, (int)Math.Round(limit * 0.7)));
        var secondaryTake = Math.Min(secondary.Count, Math.Max(0, limit - primaryTake));
        if (primaryTake + secondaryTake < limit)
        {
            primaryTake = Math.Min(primary.Count, limit - secondaryTake);
        }

        return InterleaveCatalog(primary.Take(primaryTake).ToList(), secondary.Take(secondaryTake).ToList());
    }

    private static List<BaseItem> InterleaveCatalog(IReadOnlyList<BaseItem> first, IReadOnlyList<BaseItem> second)
    {
        var mixed = new List<BaseItem>(first.Count + second.Count);
        var i = 0;
        var j = 0;
        while (i < first.Count || j < second.Count)
        {
            if (i < first.Count)
            {
                mixed.Add(first[i++]);
            }

            if (j < second.Count)
            {
                mixed.Add(second[j++]);
            }
        }

        return mixed;
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
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending),
                (ItemSortBy.PremiereDate, SortOrder.Ascending)
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
            if (constraints.UseFirstEpisodeYearForSeries)
            {
                var series = ResolveSeriesForEpisode(episode);
                if (series is not null)
                {
                    var seriesYear = GetCatalogReleaseYear(series, constraints);
                    if (!seriesYear.HasValue)
                    {
                        return true;
                    }

                    return constraints.ContainsYear(seriesYear);
                }
            }

            var episodeYear = GetReleaseYear(episode);
            if (!episodeYear.HasValue)
            {
                return true;
            }

            return constraints.ContainsYear(episodeYear);
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
        if (constraints.IsTitlePlotExcluded(item))
        {
            return false;
        }

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
        var libraryConstraints = ChannelAiRules.GetLibraryConstraints(channel);
        HolidayDefinition? holiday = null;
        if (_holidays.IsHolidayChannel(channel))
        {
            var date = scheduleDate ?? _holidays.GetScheduleDateUtc(DateTime.UtcNow);
            holiday = _holidays.GetActiveHoliday(date);
        }

        if (yearConstraints is null && genreConstraints is null && libraryConstraints is null && holiday is null
            && !_holidays.IsHolidayChannel(channel)
            && !PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            return items;
        }

        return items.Where(item =>
        {
            if (libraryConstraints is not null && !libraryConstraints.Matches(item.LibraryName))
            {
                return false;
            }
            if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel)
                && !PastTenseNewsCatalog.IsHomeMovieItem(
                    item.LibraryName,
                    item.CollectionType,
                    item.LibraryId,
                    item.Kind,
                    FinTvRuntime.Current?.Configuration.JellyfinLibraries.HomeVideoLibraryIds))
            {
                return false;
            }
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
        return GetCachedMusicAudio("all", () =>
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
            };

            return _libraryManager.GetItemsResult(query).Items.ToList();
        });
    }

    public IReadOnlyList<BaseItem> QueryMusicAudioFromLibrary(string? libraryId, string? libraryName)
    {
        var parsedId = Guid.Empty;
        var hasId = !string.IsNullOrWhiteSpace(libraryId) && Guid.TryParse(libraryId, out parsedId) && parsedId != Guid.Empty;
        var library = ResolveMusicLibrary(libraryId, libraryName);
        var parentId = library?.Id ?? (hasId ? parsedId : Guid.Empty);
        if (parentId == Guid.Empty && string.IsNullOrWhiteSpace(libraryName))
        {
            return Array.Empty<BaseItem>();
        }

        var cacheKey = parentId != Guid.Empty
            ? parentId.ToString("N")
            : "name:" + libraryName!.Trim();
        return GetCachedMusicAudio(cacheKey, () =>
        {
            var query = new InternalItemsQuery
            {
                ParentId = parentId,
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
            };

            var items = parentId == Guid.Empty
                ? new List<BaseItem>()
                : _libraryManager.GetItemsResult(query).Items.ToList();
            if (items.Count == 0 && !string.IsNullOrWhiteSpace(libraryName))
            {
                items = QueryAllMusicAudio()
                    .Where(track => string.Equals(track.LibraryName, libraryName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return items;
        });
    }

    public string? PickPlayableMusicPath(string? libraryId, string? libraryName, bool fallbackToAllMusic)
    {
        foreach (var track in Shuffle(QueryMusicAudioFromLibrary(libraryId, libraryName)))
        {
            var path = GetMediaPath(track);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        if (!fallbackToAllMusic)
        {
            return null;
        }

        foreach (var track in Shuffle(QueryAllMusicAudio()))
        {
            var path = GetMediaPath(track);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IReadOnlyList<BaseItem> Shuffle(IReadOnlyList<BaseItem> tracks)
    {
        if (tracks.Count <= 1)
        {
            return tracks;
        }

        var copy = tracks.ToList();
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    private static IReadOnlyList<BaseItem> GetCachedMusicAudio(string key, Func<IReadOnlyList<BaseItem>> load)
    {
        if (MusicAudioCache.TryGetValue(key, out var cached)
            && DateTime.UtcNow - cached.Utc < MusicAudioCacheTtl)
        {
            return cached.Tracks;
        }

        var tracks = load();
        MusicAudioCache[key] = (DateTime.UtcNow, tracks);
        return tracks;
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
        if (!string.IsNullOrWhiteSpace(libraryId) && Guid.TryParse(libraryId, out var parsedId) && parsedId != Guid.Empty)
        {
            var byId = EnumerateMusicLibraries().FirstOrDefault(folder => folder.Id == parsedId);
            if (byId is not null)
            {
                return byId;
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
        var seen = new HashSet<Guid>();
        var configuredMusic = FinTvRuntime.Current?.Configuration.JellyfinLibraries.MusicLibraryIds
            ?? [];

        foreach (var virtualFolder in _libraryManager.GetVirtualFolders())
        {
            Guid.TryParse(virtualFolder.ItemId, out var folderId);
            var typedMusic = IsMusicCollectionType(virtualFolder.CollectionType);
            var selectedMusic = folderId != Guid.Empty && configuredMusic.Contains(folderId);
            if (!typedMusic && !selectedMusic)
            {
                continue;
            }

            var folder = ResolveCollectionFolder(virtualFolder);
            if (folder is not null && seen.Add(folder.Id))
            {
                yield return folder;
            }
        }

        // Fallback for servers where virtual folder metadata is incomplete.
        var root = _libraryManager.GetUserRootFolder();
        foreach (var child in root.Children)
        {
            if (child is CollectionFolder folder
                && (IsMusicLibrary(folder) || configuredMusic.Contains(folder.Id))
                && seen.Add(folder.Id))
            {
                yield return folder;
            }
        }
    }

    private CollectionFolder? ResolveCollectionFolder(VirtualFolderInfo virtualFolder)
    {
        if (!string.IsNullOrWhiteSpace(virtualFolder.ItemId)
            && Guid.TryParse(virtualFolder.ItemId, out var parsedId)
            && parsedId != Guid.Empty)
        {
            return new CollectionFolder
            {
                Id = parsedId,
                Name = virtualFolder.Name,
                CollectionType = virtualFolder.CollectionType,
                Kind = BaseItemKind.Folder,
                LibraryId = parsedId,
                LibraryName = virtualFolder.Name
            };
        }

        if (string.IsNullOrWhiteSpace(virtualFolder.Name))
        {
            return null;
        }

        var root = _libraryManager.GetUserRootFolder();
        foreach (var child in root.Children)
        {
            if (child is CollectionFolder folder
                && folder.Name.Equals(virtualFolder.Name, StringComparison.OrdinalIgnoreCase)
                && IsMusicLibrary(folder))
            {
                return folder;
            }
        }

        return null;
    }

    private static bool IsMusicCollectionType(string? type)
        => string.Equals(type, CollectionType.music, StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, CollectionTypeOptions.music, StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsMusicLibrary(CollectionFolder folder)
        => IsMusicCollectionType(folder.CollectionType);

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

        if (slotFilter?.Tags is { Count: > 0 })
        {
            tags.AddRange(slotFilter.Tags.Where(tag =>
                !string.IsNullOrWhiteSpace(tag) && !FilterDefinition.IsFintvChannelTag(tag)));
        }

        return tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        if (!ChannelAiRules.HasCatalogConstraints(channel))
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

    private IReadOnlyList<BaseItem> ApplyMusicVideoArtistFilter(IReadOnlyList<BaseItem> items, Channel channel)
    {
        if (channel.ContentType != ChannelContentType.MusicVideo)
        {
            return items;
        }

        var allowed = _musicVideoLists.GetAllowedArtistNames(channel);
        if (allowed is null)
        {
            return items;
        }

        if (allowed.Count == 0)
        {
            return [];
        }

        return items.Where(item =>
        {
            var artist = GetMusicVideoArtist(item);
            return allowed.Any(name => MusicVideoChannelListService.ArtistsMatch(artist, name));
        }).ToList();
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
            "UR" or "NR" or "UNRATED" or "NOT RATED" or "NOTRATED" or "N/R" => null,
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
    }

    private bool TryApplyLibraryScope(InternalItemsQuery query, Channel channel)
    {
        var libraryConstraint = ChannelAiRules.GetLibraryConstraints(channel);
        if (libraryConstraint is null)
        {
            return false;
        }

        var folders = ResolveMatchingLibraryFolders(libraryConstraint);
        query.Tags = Array.Empty<string>();
        query.Genres = Array.Empty<string>();

        var selectedHomeVideo = FinTvRuntime.Current?.Configuration.JellyfinLibraries.HomeVideoLibraryIds ?? [];
        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel) && selectedHomeVideo.Count > 1)
        {
            return true;
        }

        if (folders.Count == 1 && libraryConstraint.AllLibraryNames().Count == 1)
        {
            query.ParentId = folders[0].Id;
        }

        return true;
    }

    private List<CollectionFolder> ResolveMatchingLibraryFolders(ChannelCatalogLibraryConstraints constraint)
    {
        var names = constraint.AllLibraryNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<CollectionFolder>();
        var root = _libraryManager.GetUserRootFolder();
        if (root?.Children is null)
        {
            return found;
        }

        foreach (var child in root.Children)
        {
            if (child is CollectionFolder folder
                && (names.Contains(folder.Name) || constraint.Matches(folder.Name)))
            {
                found.Add(folder);
            }
        }

        return found;
    }

    private CollectionFolder? ResolveScopedLibraryFolder(Channel channel, ChannelCatalogLibraryConstraints constraint)
    {
        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            var selected = FinTvRuntime.Current?.Configuration.JellyfinLibraries.HomeVideoLibraryIds ?? [];
            var root = _libraryManager.GetUserRootFolder();
            CollectionFolder? homeVideoFallback = null;
            foreach (var child in root.Children)
            {
                if (child is not CollectionFolder folder)
                {
                    continue;
                }

                if (selected.Count > 0 && selected.Contains(folder.Id))
                {
                    return folder;
                }

                if (constraint.AllLibraryNames().Any(name => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || PastTenseNewsCatalog.MatchesLibraryName(folder.Name))
                {
                    return folder;
                }

                if (homeVideoFallback is null && PastTenseNewsCatalog.MatchesCollectionType(folder.CollectionType))
                {
                    homeVideoFallback = folder;
                }
            }

            return homeVideoFallback;
        }

        return ResolveMatchingLibraryFolders(constraint).FirstOrDefault();
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
        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            return [BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.Episode];
        }

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
        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            return [BaseItemKind.Movie, BaseItemKind.Video];
        }

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
            SeriesId = item.SeriesId != Guid.Empty ? item.SeriesId : MapSeriesId(item),
            Title = BuildPlayoutTitle(item),
            Artist = item is MusicVideo ? GetMusicVideoArtist(item) : null,
            Duration = duration
        };
    }

    private static Guid? MapSeriesId(BaseItem item)
    {
        if (item is not Episode episode)
        {
            return null;
        }

        if (episode.SeriesId != Guid.Empty)
        {
            return episode.SeriesId;
        }

        if (episode.Series?.Id is Guid fromSeries && fromSeries != Guid.Empty)
        {
            return fromSeries;
        }

        return null;
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
        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            const string shuffleKey = "past-tense-news";
            var rng = new Random(HashCode.Combine(channel.PlayoutSeed, DateTime.UtcNow.Year, DateTime.UtcNow.DayOfYear));
            var shuffled = items
                .OrderBy(_ => rng.Next())
                .ThenBy(item => item.Id)
                .ToList();
            anchor.SeriesEpisodeIndex.TryGetValue(shuffleKey, out var index);
            if (index < 0 || index >= shuffled.Count)
            {
                index = 0;
            }

            var pick = shuffled[index];
            anchor.SeriesEpisodeIndex[shuffleKey] = (index + 1) % shuffled.Count;
            return [MapItem(pick)];
        }

        if (channel.ContentType == ChannelContentType.MusicVideo
            || catalogMode == ChannelCatalogMode.MusicVideoOnly
            || items.All(i => i is MusicVideo))
        {
            return [PickMusicVideo(items, channel, anchor)];
        }

        var useEpisodeRotation = catalogMode != ChannelCatalogMode.MovieOnly
            && items.Any(i => i is Episode);

        if (useEpisodeRotation)
        {
            var grouped = items.OfType<Episode>()
                .Where(episode => episode.SeriesId != Guid.Empty)
                .GroupBy(e => e.SeriesId)
                .ToList();

            var picks = new List<ResolvedCandidate>(grouped.Count);
            foreach (var group in grouped)
            {
                var key = group.Key.ToString("N");
                anchor.SeriesEpisodeIndex.TryGetValue(key, out var index);
                var ordered = group.OrderBy(e => e.ParentIndexNumber ?? 0).ThenBy(e => e.IndexNumber ?? 0).ToList();
                if (ordered.Count == 0)
                {
                    continue;
                }

                if (index < 0 || index >= ordered.Count)
                {
                    index = 0;
                }

                picks.Add(MapItem(ordered[index]));
            }

            if (picks.Count > 0)
            {
                return picks;
            }
        }

        return items.Select(item => MapItem(item)).Take(1).ToList();
    }

    private ResolvedCandidate PickMusicVideo(
        IReadOnlyList<BaseItem> items,
        Channel channel,
        PlayoutAnchorState anchor)
    {
        var recent = anchor.RecentMusicVideoArtists;
        var lastArtist = recent.Count > 0 ? recent[^1] : null;
        var rng = new Random(HashCode.Combine(channel.PlayoutSeed, recent.Count, items.Count));

        var ranked = items
            .Select((item, index) =>
            {
                var artist = GetMusicVideoArtist(item);
                var distance = DistanceSinceArtist(recent, artist);
                var penalty = string.Equals(artist, lastArtist, StringComparison.OrdinalIgnoreCase) ? -10_000 : 0;
                return (item, artist, score: distance + penalty, jitter: rng.Next());
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.jitter)
            .ThenBy(x => x.item.Id)
            .ToList();

        var pick = ranked[0];
        if (!string.IsNullOrWhiteSpace(pick.artist))
        {
            recent.Add(pick.artist);
            if (recent.Count > 48)
            {
                recent.RemoveRange(0, recent.Count - 48);
            }
        }

        return MapItem(pick.item);
    }

    private static int DistanceSinceArtist(List<string> recent, string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return recent.Count + 1;
        }

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(recent[i], artist, StringComparison.OrdinalIgnoreCase))
            {
                return recent.Count - 1 - i;
            }
        }

        return recent.Count + 8;
    }

    private static string GetMusicVideoArtist(BaseItem item)
    {
        if (item is MusicVideo video)
        {
            var named = video.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            if (!string.IsNullOrWhiteSpace(named))
            {
                return named.Trim();
            }
        }

        var studio = item.Studios.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (!string.IsNullOrWhiteSpace(studio))
        {
            return studio.Trim();
        }

        var name = item.Name ?? string.Empty;
        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var at = name.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0)
            {
                return name[..at].Trim();
            }
        }

        return name.Trim();
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
