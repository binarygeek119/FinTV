using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Applies fintv-* Jellyfin tags to library items using built-in channel rules.
/// </summary>
public class FinTvChannelTaggingService
{
    private static readonly string[] ParodyKeywords =
    [
        "parody", "spoof", "weird al", "yankovic", "literal video", "bart baker"
    ];

    private static readonly string[] RapKeywords =
    [
        "rap", "hip hop", "hip-hop", "hiphop", "mc ", " emcee"
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinCatalogService _catalog;
    private readonly ILogger<FinTvChannelTaggingService> _logger;

    public FinTvChannelTaggingService(
        ILibraryManager libraryManager,
        JellyfinCatalogService catalog,
        ILogger<FinTvChannelTaggingService> logger)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
        _logger = logger;
    }

    public bool IsRunning => Plugin.Instance?.Configuration.ChannelAutoTaggingTaskState.IsRunning == true;

    public ChannelAutoTaggingTaskState GetState()
        => Plugin.Instance?.Configuration.ChannelAutoTaggingTaskState ?? new ChannelAutoTaggingTaskState();

    public async Task<ChannelAutoTaggingTaskState> RunAsync(
        bool fullRetag,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var state = GetState();
        if (state.IsRunning)
        {
            return state;
        }

        state.IsRunning = true;
        state.LastError = null;
        state.LastStartedAt = DateTime.UtcNow;
        state.ProcessedItems = 0;
        state.TaggedItems = 0;
        state.SkippedItems = 0;
        SaveState(state);

        try
        {
            var items = QueryTaggableItems();
            state.TotalItems = items.Count;
            SaveState(state);

            _logger.LogInformation(
                "FinTV channel auto-tagging started: {Count} item(s), fullRetag={FullRetag}.",
                items.Count,
                fullRetag);

            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[index];

                try
                {
                    var computedTags = ComputeChannelTags(item);
                    if (!fullRetag && !NeedsTagUpdate(item, computedTags))
                    {
                        state.SkippedItems++;
                    }
                    else if (await TryApplyTagsAsync(item, computedTags, cancellationToken).ConfigureAwait(false))
                    {
                        state.TaggedItems++;
                    }
                    else
                    {
                        state.SkippedItems++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FinTV auto-tagging skipped item {ItemId} ({Name}).", item.Id, item.Name);
                    state.SkippedItems++;
                }

                state.ProcessedItems = index + 1;
                if (items.Count > 0 && (index + 1) % 25 == 0)
                {
                    SaveState(state);
                    progress?.Report((index + 1) * 100d / items.Count);
                }
            }

            state.LastCompletedAt = DateTime.UtcNow;
            state.LastError = null;
            progress?.Report(100);
            _logger.LogInformation(
                "FinTV channel auto-tagging finished: processed={Processed}, tagged={Tagged}, skipped={Skipped}.",
                state.ProcessedItems,
                state.TaggedItems,
                state.SkippedItems);
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            _logger.LogError(ex, "FinTV channel auto-tagging failed.");
            throw;
        }
        finally
        {
            state.IsRunning = false;
            SaveState(state);
        }

        return state;
    }

    public IReadOnlyList<string> ComputeChannelTags(BaseItem item)
    {
        if (item is Episode)
        {
            return Array.Empty<string>();
        }

        if (item is MusicVideo musicVideo)
        {
            return ComputeMusicVideoTags(musicVideo);
        }

        var matches = new List<string>();
        foreach (var libraryTag in ChannelAiRules.GetAutoTaggableChannelTags())
        {
            if (MatchesChannelTag(item, libraryTag))
            {
                matches.Add(libraryTag);
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryTaggableItems()
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicVideo },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        return _libraryManager.GetItemsResult(query).Items.ToList();
    }

    private bool MatchesChannelTag(BaseItem item, string libraryTag)
    {
        var rule = ChannelAiRules.GetByLibraryTag(libraryTag);
        if (rule is null)
        {
            return false;
        }

        if (!MatchesCatalogMode(item, rule.DefaultCatalogMode))
        {
            return false;
        }

        if (string.Equals(libraryTag, HolidayChannelCalendar.LibraryTag, StringComparison.OrdinalIgnoreCase))
        {
            return HolidayChannelCalendar.All.Any(holiday => HolidayChannelCalendar.MatchesHolidayContent(item, holiday));
        }

        var yearConstraints = ChannelAiRules.GetYearConstraints(libraryTag);
        if (yearConstraints is not null && !_catalog.MatchesYearConstraints(item, yearConstraints))
        {
            return false;
        }

        var genreConstraints = ChannelAiRules.GetGenreConstraints(libraryTag);
        if (genreConstraints is not null && !_catalog.MatchesGenreConstraints(item, genreConstraints))
        {
            return false;
        }

        var maxRating = ChannelAiRules.GetPresetMaxRating(libraryTag);
        if (!string.IsNullOrWhiteSpace(maxRating)
            && !RatingAtMost(item.OfficialRating, maxRating))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesCatalogMode(BaseItem item, ChannelCatalogMode catalogMode)
        => catalogMode switch
        {
            ChannelCatalogMode.MovieOnly => item is Movie,
            ChannelCatalogMode.TvOnly => item is Series,
            ChannelCatalogMode.MusicVideoOnly => item is MusicVideo,
            _ => item is Series or Movie
        };

    private static IReadOnlyList<string> ComputeMusicVideoTags(MusicVideo musicVideo)
    {
        var tags = new List<string> { "fintv-music-video" };
        var searchable = BuildSearchBlob(musicVideo);

        if (ContainsAnyKeyword(searchable, ParodyKeywords))
        {
            tags.Add("fintv-parody");
        }

        if (ContainsAnyKeyword(searchable, RapKeywords)
            || (musicVideo.Genres?.Any(genre => genre.Contains("rap", StringComparison.OrdinalIgnoreCase)
                || genre.Contains("hip hop", StringComparison.OrdinalIgnoreCase)
                || genre.Contains("hip-hop", StringComparison.OrdinalIgnoreCase)) == true))
        {
            tags.Add("fintv-rap");
        }

        return tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool NeedsTagUpdate(BaseItem item, IReadOnlyList<string> computedTags)
    {
        var existingFintv = (item.Tags ?? Array.Empty<string>())
            .Where(FilterDefinition.IsFintvChannelTag)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var desired = computedTags
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existingFintv.Count != desired.Count)
        {
            return true;
        }

        for (var index = 0; index < existingFintv.Count; index++)
        {
            if (!existingFintv[index].Equals(desired[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryApplyTagsAsync(
        BaseItem item,
        IReadOnlyList<string> computedTags,
        CancellationToken cancellationToken)
    {
        if (!NeedsTagUpdate(item, computedTags))
        {
            return false;
        }

        var preserved = (item.Tags ?? Array.Empty<string>())
            .Where(tag => !FilterDefinition.IsFintvChannelTag(tag))
            .ToList();

        var merged = preserved
            .Concat(computedTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        item.Tags = merged;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static string BuildSearchBlob(BaseItem item)
    {
        var parts = new List<string> { item.Name ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            parts.Add(item.Overview);
        }

        if (item.Genres is { Length: > 0 })
        {
            parts.Add(string.Join(' ', item.Genres));
        }

        return string.Join(' ', parts);
    }

    private static bool ContainsAnyKeyword(string blob, IEnumerable<string> keywords)
        => keywords.Any(keyword => blob.Contains(keyword, StringComparison.OrdinalIgnoreCase));

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

    private static void SaveState(ChannelAutoTaggingTaskState state)
    {
        if (Plugin.Instance is null)
        {
            return;
        }

        Plugin.Instance.Configuration.ChannelAutoTaggingTaskState = state;
        Plugin.Instance.SaveConfiguration();
    }
}
