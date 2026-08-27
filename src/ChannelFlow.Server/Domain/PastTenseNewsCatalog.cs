namespace FinTv.Domain;

/// <summary>
/// Home-movie / home-video libraries that feed the Past Tense News channel.
/// </summary>
public static class PastTenseNewsCatalog
{
    public const string ChannelTag = "channelflow-past-tense-news";

    /// <summary>
    /// Jellyfin library display name. The library type is Home Videos and Photos.
    /// </summary>
    public const string LibraryName = "Past Tense News";

    public static bool IsPastTenseNewsChannel(Channel channel)
        => FilterDefinition.PresetIdsEqual(ChannelAiRules.ExtractLibraryTag(channel.FilterJson), ChannelTag);

    public static bool MatchesLibraryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Contains("past tense", StringComparison.OrdinalIgnoreCase)
            || name.Contains("home movie", StringComparison.OrdinalIgnoreCase)
            || name.Contains("home video", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesCollectionType(string? collectionType)
    {
        var type = (collectionType ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty);
        return type is "homevideos" or "homevideo" or "homemovies" or "homemovie";
    }

    public static bool IsHomeMovieItem(
        string? libraryName,
        string? collectionType,
        Guid? libraryId,
        BaseItemKind kind,
        IReadOnlyCollection<Guid>? homeVideoLibraryIds = null)
    {
        _ = kind;
        if (MatchesLibraryName(libraryName) || MatchesCollectionType(collectionType))
        {
            return true;
        }

        return libraryId is Guid id && homeVideoLibraryIds is { Count: > 0 } && homeVideoLibraryIds.Contains(id);
    }
}
