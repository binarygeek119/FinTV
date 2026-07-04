namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Built-in AI lineup briefs for Binarygeek119 channel presets.
/// </summary>
public static class ChannelAiRules
{
    private static readonly Dictionary<string, ChannelAiRuleDefinition> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-flashback"] = new(
            "TV series and movies released from 1970 through 2010 only. For TV series, eligibility uses the first episode premiere year in that range. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed),
        ["fintv-retro"] = new(
            "TV series and movies released from 1910 through 1969 only. For TV series, eligibility uses the first episode premiere year in that range. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed),
        ["fintv-open-swim"] = new("Kids and teen shows and movies only.", ChannelCatalogMode.Mixed),
        ["fintv-reality"] = new(
            "Reality TV themed shows and movies only. Items must have a Reality genre. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed),
        ["fintv-news"] = new("News programming only from the fintv-news library tag.", ChannelCatalogMode.TvOnly),
        ["fintv-past-tense-news"] = new(
            "Only content from the Jellyfin library named Past Tense News. Schedule clips in chronological order of the events they cover and treat each story as breaking live news.",
            ChannelCatalogMode.TvOnly),
        ["fintv-crime"] = new(
            "Crime and cop themed TV shows only. Items must have a Crime, Cop, Police, or Detective genre. No game shows.",
            ChannelCatalogMode.TvOnly),
        ["fintv-comedy"] = new(
            "Comedy themed TV shows and movies only. Items must have a Comedy genre. Build a Fox-style Animation Domination block at 6pm (slots 36-41).",
            ChannelCatalogMode.Mixed),
        ["fintv-game-shows"] = new("Game shows only.", ChannelCatalogMode.TvOnly),
        ["fintv-education"] = new("Educational TV and documentaries (History, Discovery, science, nature).", ChannelCatalogMode.Mixed),
        ["fintv-youtube"] = new("YouTube-sourced content only from the fintv-youtube library tag.", ChannelCatalogMode.TvOnly),
        ["fintv-creature"] = new("Creature and monster movies.", ChannelCatalogMode.MovieOnly),
        ["fintv-hero"] = new("Hero-themed movies (superhero or otherwise).", ChannelCatalogMode.MovieOnly),
        ["fintv-funny"] = new("Comedian-led movies and TV.", ChannelCatalogMode.Mixed),
        ["fintv-holiday"] = new(
            "Seasonal holiday channel. Only play TV and movies themed to the active holiday window (up to 30 days before the observance). Match content using Jellyfin tags, plot/overview text, title keywords, and release/premiere month. When no holiday window is active the channel is off-season. Build cable-style marathons that loop smartly.",
            ChannelCatalogMode.Mixed),
        ["fintv-parody"] = new("Parody music videos; use fine-tune keywords or artists.", ChannelCatalogMode.MusicVideoOnly),
        ["fintv-rap"] = new("Rap and hip hop music videos; use fine-tune keywords or artists.", ChannelCatalogMode.MusicVideoOnly),
        ["fintv-music-video"] = new("General music videos; use fine-tune keywords or artists.", ChannelCatalogMode.MusicVideoOnly),
    };

    private static readonly Dictionary<string, ChannelCatalogYearConstraints> YearConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-flashback"] = new ChannelCatalogYearConstraints
        {
            MinYear = 1970,
            MaxYear = 2010,
            UseFirstEpisodeYearForSeries = true
        },
        ["fintv-retro"] = new ChannelCatalogYearConstraints
        {
            MinYear = 1910,
            MaxYear = 1969,
            UseFirstEpisodeYearForSeries = true
        }
    };

    private static readonly Dictionary<string, ChannelCatalogGenreConstraints> GenreConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-reality"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Reality" },
            ExcludedGenreKeywords = new[] { "Crime", "Cop", "Police", "Game Show", "Game-Show", "GameShow" }
        },
        ["fintv-crime"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective" },
            ExcludedGenreKeywords = new[] { "Game Show", "Game-Show", "GameShow" }
        },
        ["fintv-comedy"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Comedy" }
        }
    };

    private static readonly Dictionary<string, ChannelCatalogLibraryConstraints> LibraryConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-past-tense-news"] = new ChannelCatalogLibraryConstraints
        {
            LibraryName = "Past Tense News"
        }
    };

    private static readonly Dictionary<string, string> DefaultPlayoutTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-parody"] = "music-videos",
        ["fintv-rap"] = "music-videos",
        ["fintv-music-video"] = "music-videos",
        ["fintv-youtube"] = "youtube-pbs",
        ["fintv-game-shows"] = "winning-game-shows",
        ["fintv-education"] = "get-learneded",
        ["fintv-past-tense-news"] = "past-tense-news",
        ["fintv-open-swim"] = "kids-all-day",
        ["fintv-flashback"] = "classic-cable",
        ["fintv-retro"] = "classic-cable",
        ["fintv-creature"] = "movie-marathon",
        ["fintv-hero"] = "movie-marathon",
        ["fintv-holiday"] = "holiday-channel",
        ["fintv-comedy"] = "slappy-comedy",
    };

    /// <summary>
    /// Gets the AI rule for a library tag, if defined.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Rule definition or null.</returns>
    public static ChannelAiRuleDefinition? GetByLibraryTag(string? libraryTag)
    {
        if (string.IsNullOrWhiteSpace(libraryTag))
        {
            return null;
        }

        return Rules.TryGetValue(libraryTag, out var rule) ? rule : null;
    }

    /// <summary>
    /// Gets the AI rule brief text for a library tag.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Rule brief or empty string.</returns>
    public static string GetBrief(string? libraryTag)
        => GetByLibraryTag(libraryTag)?.Brief ?? string.Empty;

    /// <summary>
    /// Gets the recommended AI playout template for a library tag, if any.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Template id or null.</returns>
    public static string? GetDefaultPlayoutTemplateId(string? libraryTag)
    {
        if (string.IsNullOrWhiteSpace(libraryTag))
        {
            return null;
        }

        return DefaultPlayoutTemplates.TryGetValue(libraryTag, out var templateId) ? templateId : null;
    }

    /// <summary>
    /// Gets optional release-year constraints for catalog and AI filtering.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Year constraints or null.</returns>
    public static ChannelCatalogYearConstraints? GetYearConstraints(string? libraryTag)
    {
        if (string.IsNullOrWhiteSpace(libraryTag))
        {
            return null;
        }

        return YearConstraints.TryGetValue(libraryTag, out var constraints) ? constraints : null;
    }

    /// <summary>
    /// Gets year constraints for a channel from its filter tag.
    /// </summary>
    /// <param name="channel">Channel entity.</param>
    /// <returns>Year constraints or null.</returns>
    public static ChannelCatalogYearConstraints? GetYearConstraints(Channel channel)
    {
        var fromTag = GetYearConstraints(ExtractLibraryTag(channel.FilterJson));
        var fromFilter = GetYearConstraintsFromFilter(channel.FilterJson);
        return fromTag ?? fromFilter;
    }

    /// <summary>
    /// Gets optional genre/theme constraints for catalog and AI filtering.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Genre constraints or null.</returns>
    public static ChannelCatalogGenreConstraints? GetGenreConstraints(string? libraryTag)
    {
        if (string.IsNullOrWhiteSpace(libraryTag))
        {
            return null;
        }

        return GenreConstraints.TryGetValue(libraryTag, out var constraints) ? constraints : null;
    }

    /// <summary>
    /// Gets genre constraints for a channel from its filter tag.
    /// </summary>
    /// <param name="channel">Channel entity.</param>
    /// <returns>Genre constraints or null.</returns>
    public static ChannelCatalogGenreConstraints? GetGenreConstraints(Channel channel)
        => GetGenreConstraints(ExtractLibraryTag(channel.FilterJson));

    /// <summary>
    /// Gets optional Jellyfin library folder constraints for catalog and AI filtering.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Library constraints or null.</returns>
    public static ChannelCatalogLibraryConstraints? GetLibraryConstraints(string? libraryTag)
    {
        if (string.IsNullOrWhiteSpace(libraryTag))
        {
            return null;
        }

        return LibraryConstraints.TryGetValue(libraryTag, out var constraints) ? constraints : null;
    }

    /// <summary>
    /// Gets library constraints for a channel from its filter tag.
    /// </summary>
    /// <param name="channel">Channel entity.</param>
    /// <returns>Library constraints or null.</returns>
    public static ChannelCatalogLibraryConstraints? GetLibraryConstraints(Channel channel)
        => GetLibraryConstraints(ExtractLibraryTag(channel.FilterJson));

    /// <summary>
    /// Whether the channel has catalog filters beyond library tags.
    /// </summary>
    /// <param name="channel">Channel entity.</param>
    /// <returns>True when year, genre, or library constraints apply.</returns>
    public static bool HasCatalogConstraints(Channel channel)
        => GetYearConstraints(channel) is not null
            || GetGenreConstraints(channel) is not null
            || GetLibraryConstraints(channel) is not null
            || HolidayChannelCalendar.IsHolidayChannel(channel);

    /// <summary>
    /// Resolves catalog mode from channel state and optional library tag.
    /// </summary>
    /// <param name="channel">Channel entity.</param>
    /// <param name="libraryTag">Optional library tag override.</param>
    /// <returns>Effective catalog mode.</returns>
    public static ChannelCatalogMode ResolveCatalogMode(Channel channel, string? libraryTag = null)
    {
        if (channel.CatalogMode.HasValue)
        {
            return channel.CatalogMode.Value;
        }

        var tagRule = GetByLibraryTag(libraryTag ?? ExtractLibraryTag(channel.FilterJson));
        if (tagRule is not null)
        {
            return tagRule.DefaultCatalogMode;
        }

        return channel.ContentType switch
        {
            ChannelContentType.Movie => ChannelCatalogMode.MovieOnly,
            ChannelContentType.MusicVideo => ChannelCatalogMode.MusicVideoOnly,
            _ => ChannelCatalogMode.TvOnly
        };
    }

    /// <summary>
    /// Extracts the first fintv tag from channel filter JSON.
    /// </summary>
    /// <param name="filterJson">Channel filter JSON.</param>
    /// <returns>Library tag or null.</returns>
    public static string? ExtractLibraryTag(string? filterJson)
        => FilterDefinition.ExtractFintvLibraryTag(filterJson);

    /// <summary>
    /// Gets year constraints encoded directly in channel filter JSON.
    /// </summary>
    /// <param name="filterJson">Channel filter JSON.</param>
    /// <returns>Year constraints or null.</returns>
    public static ChannelCatalogYearConstraints? GetYearConstraintsFromFilter(string? filterJson)
    {
        var filter = FilterDefinition.Parse(filterJson);
        if (filter?.MinYear is null && filter?.MaxYear is null)
        {
            return null;
        }

        return new ChannelCatalogYearConstraints
        {
            MinYear = filter.MinYear ?? 1888,
            MaxYear = filter.MaxYear ?? DateTime.UtcNow.Year + 1,
            UseFirstEpisodeYearForSeries = true
        };
    }

    /// <summary>
    /// Library tags excluded from the AI lineup tab and bulk generate.
    /// </summary>
    public static bool IsExcludedFromAi(string? libraryTag)
        => string.Equals(libraryTag, "fintv-news", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// AI rule metadata for a channel preset.
/// </summary>
public class ChannelAiRuleDefinition
{
    public ChannelAiRuleDefinition(string brief, ChannelCatalogMode defaultCatalogMode)
    {
        Brief = brief;
        DefaultCatalogMode = defaultCatalogMode;
    }

    public string Brief { get; }

    public ChannelCatalogMode DefaultCatalogMode { get; }
}

/// <summary>
/// Release-year limits for channel catalog and AI manifests.
/// </summary>
public class ChannelCatalogYearConstraints
{
    public int MinYear { get; set; }

    public int MaxYear { get; set; }

    /// <summary>
    /// When true, series eligibility uses the first episode premiere/production year.
    /// </summary>
    public bool UseFirstEpisodeYearForSeries { get; set; }

    public bool ContainsYear(int? year)
        => year.HasValue && year.Value >= MinYear && year.Value <= MaxYear;
}

/// <summary>
/// Genre/theme limits for channel catalog and AI manifests.
/// </summary>
public class ChannelCatalogGenreConstraints
{
    public IReadOnlyList<string> RequiredGenreKeywords { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedGenreKeywords { get; set; } = Array.Empty<string>();

    public bool Matches(IReadOnlyList<string>? genres)
    {
        genres ??= Array.Empty<string>();
        if (genres.Count == 0)
        {
            return RequiredGenreKeywords.Count == 0;
        }

        if (RequiredGenreKeywords.Count > 0
            && !genres.Any(genre => RequiredGenreKeywords.Any(keyword =>
                genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (ExcludedGenreKeywords.Count > 0
            && genres.Any(genre => ExcludedGenreKeywords.Any(keyword =>
                genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Jellyfin library folder limits for channel catalog and AI manifests.
/// </summary>
public class ChannelCatalogLibraryConstraints
{
    public string LibraryName { get; set; } = string.Empty;
}
