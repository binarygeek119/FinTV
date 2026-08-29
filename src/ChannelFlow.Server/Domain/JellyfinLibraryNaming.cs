namespace FinTv.Domain;

/// <summary>
/// Maps Jellyfin library display names to collection types when the server omits CollectionType.
/// "TV Shows" is checked before "show" so "Stand-Up Comedies Movies" stays movies.
/// </summary>
public static class JellyfinLibraryNaming
{
    public static string? GuessCollectionType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (PastTenseNewsCatalog.MatchesLibraryName(name))
        {
            return "homevideos";
        }

        if (name.Contains("tv show", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tvshows", StringComparison.OrdinalIgnoreCase)
            || name.Contains("television", StringComparison.OrdinalIgnoreCase))
        {
            return "tvshows";
        }

        if (name.Contains("movie", StringComparison.OrdinalIgnoreCase)
            || name.Contains("film", StringComparison.OrdinalIgnoreCase))
        {
            return "movies";
        }

        if (name.Contains("music video", StringComparison.OrdinalIgnoreCase))
        {
            return "musicvideos";
        }

        if (name.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return "music";
        }

        if (name.Contains("tv", StringComparison.OrdinalIgnoreCase)
            || name.Contains("series", StringComparison.OrdinalIgnoreCase)
            || name.Contains("show", StringComparison.OrdinalIgnoreCase))
        {
            return "tvshows";
        }

        return null;
    }

    public static string? GroupFromCollectionType(string? collectionType)
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

    public static string? GroupFromLibrary(string? collectionType, string? name)
        => GroupFromCollectionType(collectionType)
            ?? GroupFromCollectionType(GuessCollectionType(name));
}
