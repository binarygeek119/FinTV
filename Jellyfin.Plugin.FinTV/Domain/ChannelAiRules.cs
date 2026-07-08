namespace Jellyfin.Plugin.FinTV.Domain;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

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
            "Reality TV themed shows and movies. Match Reality genre or reality/competition keywords in title, plot, or tags. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed),
        ["fintv-news"] = new("News programming only (News genre preferred).", ChannelCatalogMode.TvOnly),
        ["fintv-past-tense-news"] = new(
            "Only content from the Jellyfin library named Past Tense News. Schedule clips in chronological order of the events they cover and treat each story as breaking live news.",
            ChannelCatalogMode.TvOnly),
        ["fintv-crime"] = new(
            "Crime and cop themed TV shows and movies. Match Crime/Cop/Police/Detective genres or crime-related plot/overview text. No game shows.",
            ChannelCatalogMode.Mixed),
        ["fintv-comedy"] = new(
            "Comedy themed TV shows and movies. Match Comedy genre or comedy keywords in title, plot, or tags. Build a Fox-style Animation Domination block at 6pm (slots 36-41).",
            ChannelCatalogMode.Mixed),
        ["fintv-game-shows"] = new("Game shows only.", ChannelCatalogMode.Mixed),
        ["fintv-education"] = new("Educational TV and documentaries (History, Discovery, science, nature).", ChannelCatalogMode.Mixed),
        ["fintv-youtube"] = new("Only content from the Jellyfin TV library named YouTube.", ChannelCatalogMode.TvOnly),
        ["fintv-creature"] = new(
            "Creature and monster movies and TV. Match Horror/Sci-Fi/Monster genres or creature/monster keywords in title, plot, and tags.",
            ChannelCatalogMode.Mixed),
        ["fintv-hero"] = new(
            "Hero-themed movies and TV about anyone who saves or protects people — superheroes, first responders, doctors, rescuers, soldiers, and everyday heroes. Prefer uplifting stories of courage and rescue. Match relevant genres or save/rescue/hero keywords in title, plot, and tags.",
            ChannelCatalogMode.Mixed),
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
        ["fintv-flashback"] = new ChannelCatalogGenreConstraints
        {
            ExcludedGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective", "Game Show", "Game-Show", "GameShow" }
        },
        ["fintv-retro"] = new ChannelCatalogGenreConstraints
        {
            ExcludedGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective", "Game Show", "Game-Show", "GameShow" }
        },
        ["fintv-open-swim"] = new ChannelCatalogGenreConstraints
        {
            ExcludedGenreKeywords = new[] { "Horror", "Thriller", "Crime", "War", "Mystery" }
        },
        ["fintv-reality"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Reality" },
            RequiredPlotKeywords = new[]
            {
                "reality tv", "reality show", "reality competition", "competition series",
                "survivor", "big brother", "bachelor", "bachelorette", "real housewives",
                "real world", "top chef", "project runway", "american idol", "the voice",
                "dance moms", "love island", "storage wars", "pawn stars", "duck dynasty",
                "keeping up with", "married at first sight", "90 day", "house hunters",
                "naked and afraid", "alone", "gold rush", "deadliest catch", "survivorman"
            },
            ExcludedGenreKeywords = new[] { "Crime", "Cop", "Police", "Game Show", "Game-Show", "GameShow" }
        },
        ["fintv-crime"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective" },
            RequiredPlotKeywords = new[]
            {
                "crime", "criminal", "cop", "cops", "police", "detective", "robbery", "robber",
                "heist", "murder", "homicide", "investigation", "undercover", "fbi", "cia",
                "gangster", "mob", "mafia", "prison", "prosecutor", "law enforcement", "sheriff",
                "arson", "kidnapping", "larceny", "felony", "swat", "forensic"
            },
            ExcludedGenreKeywords = new[] { "Game Show", "Game-Show", "GameShow" }
        },
        ["fintv-comedy"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Comedy" },
            RequiredPlotKeywords = new[]
            {
                "comedy", "comedic", "comedian", "stand-up", "standup", "sitcom",
                "sketch comedy", "late night", "funny", "humor", "humour", "parody",
                "satire", "slapstick", "rom-com", "romcom"
            }
        },
        ["fintv-funny"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Comedy" },
            RequiredPlotKeywords = new[]
            {
                "comedy", "comedic", "comedian", "stand-up", "standup", "sitcom",
                "sketch comedy", "late night", "funny", "humor", "humour"
            }
        },
        ["fintv-game-shows"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Game Show", "Game-Show", "GameShow", "Quiz", "Trivia" },
            RequiredPlotKeywords = new[]
            {
                "game show", "quiz show", "trivia", "contestant", "prize money",
                "wheel of fortune", "jeopardy", "family feud", "price is right",
                "match game", "password", "millionaire", "deal or no deal",
                "who wants to be", "are you smarter", "press your luck", "hollywood squares"
            }
        },
        ["fintv-education"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Documentary", "Educational", "Education", "History", "Science", "Nature" },
            RequiredPlotKeywords = new[]
            {
                "documentary", "educational", "history", "science", "nature", "wildlife",
                "planet earth", "cosmos", "universe", "archaeology", "biology", "physics",
                "geography", "anthropology", "exploration", "discovery", "learn", "lecture",
                "how it works", "engineering", "technology", "invention", "ancient"
            }
        },
        ["fintv-creature"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Horror", "Sci-Fi", "Science Fiction", "Monster", "Creature", "Thriller", "Fantasy" },
            RequiredPlotKeywords = new[]
            {
                "monster", "creature", "beast", "kaiju", "godzilla", "alien", "extraterrestrial", "ufo",
                "vampire", "werewolf", "wolfman", "zombie", "undead", "ghoul", "demon", "devil",
                "mutant", "abomination", "sea monster", "giant shark", "dinosaur", "dragon",
                "frankenstein", "mummy", "cryptid", "bigfoot", "sasquatch", "tentacle", "blob",
                "creature feature", "double feature", "body horror", "giant spider", "giant ant",
                "king kong", "gill-man", "swamp thing", "loch ness", "yeti", "reanimated"
            }
        },
        ["fintv-hero"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[]
            {
                "Action", "Adventure", "Superhero", "Fantasy", "Sci-Fi", "Science Fiction",
                "Drama", "Medical", "Hospital", "War", "Biography"
            },
            RequiredPlotKeywords = new[]
            {
                "hero", "heroine", "heroes", "savior", "saviour", "save lives", "saves lives",
                "saved lives", "saving lives", "save the", "saves the", "saved the", "saving the",
                "rescue", "rescues", "rescued", "rescuing", "protector", "protects", "protecting",
                "defender", "defends", "champion", "guardian", "crusader", "brave", "courage",
                "selfless", "sacrifice", "first responder", "firefighter", "fireman", "fire fighter",
                "paramedic", "lifeguard", "EMT", "medic", "doctor", "surgeon", "nurse",
                "superhero", "super hero", "superpower", "super power", "vigilante",
                "superman", "batman", "wonder woman", "spider-man", "spiderman", "iron man",
                "captain america", "avenger", "marvel", "x-men", "war hero", "medal of honor",
                "unsung hero", "everyday hero", "disaster relief", "against all odds"
            }
        },
        ["fintv-news"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "News", "Newscast", "Journalism" }
        }
    };

    private static readonly Dictionary<string, ChannelCatalogLibraryConstraints> LibraryConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-past-tense-news"] = new ChannelCatalogLibraryConstraints
        {
            LibraryName = "Past Tense News"
        },
        ["fintv-youtube"] = new ChannelCatalogLibraryConstraints
        {
            LibraryName = "YouTube"
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
    {
        if (GetYearConstraints(channel) is not null
            || GetGenreConstraints(channel) is not null
            || GetLibraryConstraints(channel) is not null
            || HolidayChannelCalendar.IsHolidayChannel(channel))
        {
            return true;
        }

        var filter = FilterDefinition.Parse(channel.FilterJson);
        return filter is not null
            && (!string.IsNullOrWhiteSpace(filter.MinRating)
                || !string.IsNullOrWhiteSpace(filter.MaxRating)
                || !string.IsNullOrWhiteSpace(filter.TitleContains));
    }

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

    public IReadOnlyList<string> RequiredPlotKeywords { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedGenreKeywords { get; set; } = Array.Empty<string>();

    public bool Matches(IReadOnlyList<string>? genres)
    {
        genres ??= Array.Empty<string>();

        if (ExcludedGenreKeywords.Count > 0
            && genres.Any(genre => ExcludedGenreKeywords.Any(keyword =>
                genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (RequiredGenreKeywords.Count == 0 && RequiredPlotKeywords.Count == 0)
        {
            return true;
        }

        if (RequiredGenreKeywords.Count > 0
            && genres.Any(genre => RequiredGenreKeywords.Any(keyword =>
                genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return RequiredGenreKeywords.Count == 0;
    }

    public bool MatchesItem(BaseItem item)
    {
        if (item is Episode)
        {
            return false;
        }

        var genres = item.Genres?.ToList() ?? new List<string>();

        if (ExcludedGenreKeywords.Count > 0
            && genres.Any(genre => ExcludedGenreKeywords.Any(keyword =>
                genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (RequiredGenreKeywords.Count == 0 && RequiredPlotKeywords.Count == 0)
        {
            return true;
        }

        if (RequiredGenreKeywords.Count > 0
            && genres.Any(genre => RequiredGenreKeywords.Any(keyword =>
                genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (RequiredPlotKeywords.Count > 0 && MatchesPlotOrTitle(item))
        {
            return true;
        }

        return false;
    }

    private bool MatchesPlotOrTitle(BaseItem item)
    {
        var searchable = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            searchable.Add(item.Name);
        }

        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            searchable.Add(item.Overview);
        }

        var itemTags = item.Tags?.ToList();
        if (itemTags is { Count: > 0 })
        {
            searchable.Add(string.Join(' ', itemTags));
        }

        if (searchable.Count == 0)
        {
            return false;
        }

        var blob = string.Join(' ', searchable);
        return RequiredPlotKeywords.Any(keyword =>
            blob.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Jellyfin library folder limits for channel catalog and AI manifests.
/// </summary>
public class ChannelCatalogLibraryConstraints
{
    public string LibraryName { get; set; } = string.Empty;
}
