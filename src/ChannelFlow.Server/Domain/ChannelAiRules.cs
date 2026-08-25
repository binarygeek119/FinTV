namespace FinTv.Domain;

using System.Text.RegularExpressions;

/// <summary>
/// Built-in AI lineup briefs for Binarygeek119 channel presets.
/// </summary>
public static class ChannelAiRules
{
    private const string CatalogMatchRule =
        "This is the target tone. Pick the closest titles from the filtered catalog. If the library has no exact match, use the nearest theme from the pool (for example no 90s soaps → other Mid-Day family/drama). Do not invent titles. Ratings are preferences, not hard catalog cuts.";

    private static readonly Dictionary<string, ChannelAiRuleDefinition> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelflow-flashback"] = new(
            "TV series and movies released from 1970 through 2010 only. For TV series, eligibility uses the first episode premiere year in that range. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed,
            BuildFlashBackDaypartGuide()),
        ["channelflow-retro"] = new(
            "TV series and movies released from 1910 through 1969 only. For TV series, eligibility uses the first episode premiere year in that range. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed,
            BuildRetroDaypartGuide()),
        ["channelflow-open-swim"] = new(
            "Kids and teen TV and movies from any release year (no year cap). Only kid-rated content up to TV-PG. Prioritize Nickelodeon, Disney Channel, Fox Kids, and Cartoon Network style cartoons and live-action kids shows from any era. Exclude horror, crime, war, and adult thriller genres.",
            ChannelCatalogMode.Mixed),
        ["channelflow-reality"] = new(
            "Reality TV themed shows and movies. Match Reality genre or reality/competition keywords in title, plot, or tags. Exclude crime, cops, and game shows.",
            ChannelCatalogMode.Mixed,
            BuildFlipTelevisionDaypartGuide()),
        ["channelflow-past-tense-news"] = new(
            "Home movies and home videos from the Past Tense News / Home Movies / Home Videos library. Shuffle clips at random and present every clip as live breaking news.",
            ChannelCatalogMode.Mixed),
        ["channelflow-crime"] = new(
            "Crime and cop themed TV shows and movies. Match Crime/Cop/Police/Detective genres or crime-related plot/overview text. Exclude animated comedies, game shows, and spy-comedy series that only mention CIA/FBI without crime themes.",
            ChannelCatalogMode.Mixed,
            BuildCopsAndRobbersDaypartGuide()),
        ["channelflow-comedy"] = new(
            "Comedy themed TV shows and movies. Match Comedy genre or comedy keywords in title, plot, or tags. Friday 5:00-8:00pm is Slappy's Toon Takeover (kid cartoons only).",
            ChannelCatalogMode.Mixed,
            BuildSlappyDaypartGuide()),
        ["channelflow-game-shows"] = new("Game shows only.", ChannelCatalogMode.Mixed),
        ["channelflow-education"] = new(
            "Educational TV and documentaries (History, Discovery, science, nature).",
            ChannelCatalogMode.Mixed,
            BuildGetLearnededDaypartGuide()),
        ["channelflow-youtube"] = new("Only content from the Jellyfin TV library named YouTube.", ChannelCatalogMode.TvOnly),
        ["channelflow-creature"] = new(
            "Creature and monster movies and TV. Match Horror/Sci-Fi/Monster genres or creature/monster keywords in title, plot, and tags.",
            ChannelCatalogMode.Mixed),
        ["channelflow-hero"] = new(
            "Hero-themed movies and TV about anyone who saves or protects people — superheroes, first responders, doctors, rescuers, soldiers, and everyday heroes. Prefer uplifting stories of courage and rescue. Match relevant genres or save/rescue/hero keywords in title, plot, and tags.",
            ChannelCatalogMode.Mixed),
        ["channelflow-funny"] = new("Comedian-led movies and TV.", ChannelCatalogMode.Mixed),
        ["channelflow-holiday"] = new(
            "Seasonal holiday channel. Only play TV and movies themed to the active holiday window (up to 30 days before the observance). Match content using Jellyfin tags, plot/overview text, title keywords, and release/premiere month. When no holiday window is active the channel is off-season. Build cable-style marathons that loop smartly.",
            ChannelCatalogMode.Mixed),
        ["channelflow-parody"] = new("Parody music videos; use fine-tune keywords or artists.", ChannelCatalogMode.MusicVideoOnly),
        ["channelflow-rap"] = new("Rap and hip hop music videos; use fine-tune keywords or artists.", ChannelCatalogMode.MusicVideoOnly),
        ["channelflow-music-video"] = new("General music videos; use fine-tune keywords or artists.", ChannelCatalogMode.MusicVideoOnly),
    };

    private static readonly Dictionary<string, ChannelCatalogYearConstraints> YearConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelflow-flashback"] = new ChannelCatalogYearConstraints
        {
            MinYear = 1970,
            MaxYear = 2010,
            UseFirstEpisodeYearForSeries = true
        },
        ["channelflow-retro"] = new ChannelCatalogYearConstraints
        {
            MinYear = 1910,
            MaxYear = 1969,
            UseFirstEpisodeYearForSeries = true
        }
    };

    private static readonly Dictionary<string, ChannelCatalogGenreConstraints> GenreConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelflow-flashback"] = new ChannelCatalogGenreConstraints
        {
            ExcludedGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective", "Game Show", "Game-Show", "GameShow" }
        },
        ["channelflow-retro"] = new ChannelCatalogGenreConstraints
        {
            ExcludedGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective", "Game Show", "Game-Show", "GameShow" }
        },
        ["channelflow-open-swim"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Kids", "Family", "Children", "Animation", "Animated", "Cartoon", "Preschool" },
            RequiredPlotKeywords = new[]
            {
                "nickelodeon", "nicktoons", "nick jr", "disney", "disney channel", "disney junior", "playhouse disney",
                "cartoon network", "fox kids", "fox box", "kids wb", "wb kids", "saturday morning", "after school",
                "spongebob", "rugrats", "doug", "hey arnold", "catdog", "rocko's modern life", "fairly oddparents",
                "avatar", "legend of korra", "invader zim", "drake and josh", "icarly", "victorious", "blue's clues",
                "dora the explorer", "paw patrol", "kim possible", "phineas and ferb", "gravity falls", "ducktales",
                "darkwing duck", "goof troop", "recess", "lizzie mcguire", "suite life", "mickey mouse", "winnie the pooh",
                "dexter's laboratory", "powerpuff girls", "cow and chicken", "ed edd n eddy", "johnny bravo",
                "courage the cowardly dog", "adventure time", "steven universe", "amazing world of gumball",
                "teen titans", "samurai jack", "codename kids next door", "foster's home", "ben 10", "animaniacs",
                "tiny toon", "scooby-doo", "looney tunes", "sesame street", "mister rogers", "mr. rogers", "peppa pig",
                "clarissa", "all that", "kenan and kel", "goosebumps", "mighty morphin", "ninja turtles"
            },
            ExcludedGenreKeywords = new[] { "Horror", "Thriller", "Crime", "War" }
        },
        ["channelflow-reality"] = new ChannelCatalogGenreConstraints
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
        ["channelflow-crime"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Crime", "Cop", "Police", "Detective" },
            RequiredPlotKeywords = new[]
            {
                "crime", "criminal", "cop", "cops", "police", "detective", "robbery", "robber",
                "heist", "murder", "homicide", "investigation", "undercover", "fbi", "cia",
                "gangster", "mob", "mafia", "prison", "prosecutor", "law enforcement", "sheriff",
                "arson", "kidnapping", "larceny", "felony", "swat", "forensic"
            },
            RestrictedPlotKeywords = new[] { "cia", "fbi", "undercover", "investigation" },
            PlotMatchSupportingGenreKeywords = new[]
            {
                "Crime", "Cop", "Police", "Detective", "Thriller", "Mystery", "Suspense", "Drama", "Action"
            },
            ExcludedGenreKeywords = new[]
            {
                "Game Show", "Game-Show", "GameShow",
                "Animation", "Animated", "Cartoon", "Anime"
            }
        },
        ["channelflow-comedy"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Comedy" },
            RequiredPlotKeywords = new[]
            {
                "comedy", "comedic", "comedian", "stand-up", "standup", "sitcom",
                "sketch comedy", "late night", "funny", "humor", "humour", "parody",
                "satire", "slapstick", "rom-com", "romcom"
            }
        },
        ["channelflow-funny"] = new ChannelCatalogGenreConstraints
        {
            RequiredGenreKeywords = new[] { "Comedy" },
            RequiredPlotKeywords = new[]
            {
                "comedy", "comedic", "comedian", "stand-up", "standup", "sitcom",
                "sketch comedy", "late night", "funny", "humor", "humour"
            }
        },
        ["channelflow-game-shows"] = new ChannelCatalogGenreConstraints
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
        ["channelflow-education"] = new ChannelCatalogGenreConstraints
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
        ["channelflow-creature"] = new ChannelCatalogGenreConstraints
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
        ["channelflow-hero"] = new ChannelCatalogGenreConstraints
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
        }
    };

    private static readonly Dictionary<string, ChannelCatalogLibraryConstraints> LibraryConstraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelflow-past-tense-news"] = new ChannelCatalogLibraryConstraints
        {
            LibraryName = "Home Movies",
            AlternateLibraryNames = ["Past Tense News", "Home Movie", "Home Videos", "Home Video"]
        },
        ["channelflow-youtube"] = new ChannelCatalogLibraryConstraints
        {
            LibraryName = "YouTube"
        }
    };

    private static readonly Dictionary<string, string> DefaultPlayoutTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelflow-parody"] = "music-videos",
        ["channelflow-rap"] = "music-videos",
        ["channelflow-music-video"] = "music-videos",
        ["channelflow-youtube"] = "youtube-pbs",
        ["channelflow-game-shows"] = "winning-game-shows",
        ["channelflow-education"] = "get-learneded",
        ["channelflow-past-tense-news"] = "past-tense-news",
        ["channelflow-open-swim"] = "kids-all-day",
        ["channelflow-flashback"] = "classic-cable",
        ["channelflow-retro"] = "classic-cable",
        ["channelflow-reality"] = "classic-cable",
        ["channelflow-crime"] = "classic-cable",
        ["channelflow-creature"] = "movie-marathon",
        ["channelflow-hero"] = "movie-marathon",
        ["channelflow-funny"] = "movie-marathon",
        ["channelflow-holiday"] = "holiday-channel",
        ["channelflow-comedy"] = "slappy-comedy",
    };

    private static string BuildFlashBackDaypartGuide() => NetworkDaypartGuide(
        "FlashBack TV (1970s–2000s)",
        ("Early Bird", "(TV-MA / R) Reruns of yesterday's blockbuster movies."),
        ("Before School", "(TV-Y7 / G) 80s Saturday-morning cartoon shorts."),
        ("Morning", "(TV-G / PG) Family sitcoms from the 70s/80s."),
        ("Mid-Day", "(TV-PG / PG) Original curated 90s soap-opera arcs."),
        ("After School", "(TV-G / PG) 90s/00s golden-era animation."),
        ("Teen Hour", "(TV-14 / PG-13) 90s teen dramas and coming-of-age movies."),
        ("Prime Time", "(TV-MA / R) Blockbuster movies (1970-2000)."),
        ("Late Night", "(TV-MA / UR) Uncut cult classics, lost media, and director's cuts."));

    private static string BuildRetroDaypartGuide() => NetworkDaypartGuide(
        "Retro TV (1910s–1960s)",
        ("Early Bird", "(TV-PG / PG) Reruns of yesterday's Hollywood features."),
        ("Before School", "(TV-G / G) Early animation shorts (Popeye, Betty Boop)."),
        ("Morning", "(TV-G / G) Mid-century variety shows."),
        ("Mid-Day", "(TV-G / G) Early sitcoms and radio-style plays."),
        ("After School", "(TV-G / PG) 50s/60s animated series."),
        ("Teen Hour", "(TV-PG / PG) Retro youth-culture films."),
        ("Prime Time", "(TV-PG / PG) Golden Age of Hollywood features."),
        ("Late Night", "(TV-PG / PG-13) Film noir, psychological thrillers, and avant-garde early cinema."));

    private static string BuildFlipTelevisionDaypartGuide() => NetworkDaypartGuide(
        "Flip Television (reality)",
        ("Early Bird", "(TV-MA / R) Reruns of yesterday's survival/elimination shows."),
        ("Before School", "(TV-PG / PG) Short-form competition clips."),
        ("Morning", "(TV-G / PG) Home improvement and cooking."),
        ("Mid-Day", "(TV-14 / PG-13) Lifestyle vlogs and dating competitions."),
        ("After School", "(TV-PG / PG) Casual, light-hearted reality."),
        ("Teen Hour", "(TV-14 / PG-13) Influencer competitions and teen reality."),
        ("Prime Time", "(TV-MA / R) High-stakes elimination and survival shows."),
        ("Late Night", "(TV-MA / R) Unfiltered after-dark reunions and raw behind-the-scenes."));

    private static string BuildCopsAndRobbersDaypartGuide() => NetworkDaypartGuide(
        "Cops And Robbers (crime)",
        ("Early Bird", "(TV-MA / R) Reruns of yesterday's gritty crime dramas."),
        ("Before School", "(TV-PG / PG) Light mystery shorts and detective tips."),
        ("Morning", "(TV-PG / PG) Classic whodunnit mysteries."),
        ("Mid-Day", "(TV-14 / PG-13) Police procedurals and forensic studies."),
        ("After School", "(TV-Y7 / PG) Animated crime-solving."),
        ("Teen Hour", "(TV-14 / PG-13) YA crime dramas and teen detectives."),
        ("Prime Time", "(TV-MA / R) Gritty modern crime dramas and forensics."),
        ("Late Night", "(TV-MA / UR) Raw police bodycam footage and unsolved cold-case deep dives."));

    private static string BuildSlappyDaypartGuide() => NetworkDaypartGuide(
        "Slappy (comedy)",
        ("Early Bird", "(TV-MA / R) Reruns of yesterday's roast/sketch specials."),
        ("Before School", "(TV-G / G) Classic slapstick animated shorts."),
        ("Morning", "(TV-G / G) Clean stand-up and family sketches."),
        ("Mid-Day", "(TV-PG / PG) Thematic sitcom marathons."),
        ("After School", "(TV-G / PG) All-ages comedy cartoons."),
        ("Teen Hour", "(TV-14 / PG-13) Mon-Thu teen sitcoms and edgy sketch comedy. Friday 5:00-8:00pm is Toon Takeover instead."),
        ("Prime Time", "(TV-MA / R) Mon-Thu roast specials and uncensored sketch. Friday 5:00-8:00pm is Toon Takeover; 8:00-10:00pm continues uncensored comedy."),
        ("Late Night", "(TV-MA / UR) Uncensored adult stand-up and surrealist experimental comedy."),
        ("Slappy's Toon Takeover", "Friday only, 5:00-8:00pm (slots 34-39): 3-hour kid-cartoon event (TV-Y7/TV-G, G/PG). Use days [\"fri\"]. Not adult animation and not Mon-Thu."));

    private static string BuildGetLearnededDaypartGuide() => NetworkDaypartGuide(
        "GET LEARNEDED (educational)",
        ("Early Bird", "(TV-PG / PG) Reruns of yesterday's university lectures."),
        ("Before School", "(TV-Y / G) Fact of the Day animated shorts."),
        ("Morning", "(TV-G / G) Science for kids and basic tutorials."),
        ("Mid-Day", "(TV-G / PG) Nature and history documentaries."),
        ("After School", "(TV-G / PG) Educational cartoons."),
        ("Teen Hour", "(TV-PG / PG-13) Advanced study guides and philosophy."),
        ("Prime Time", "(TV-PG / PG) University-level lectures and deep-dive essays."),
        ("Late Night", "(TV-PG / TV-14) Complex sociology, dark history, and metaphysical theories."));

    private static string NetworkDaypartGuide(string heading, params (string Block, string Line)[] rows)
    {
        var lines = new List<string>
        {
            heading,
            CatalogMatchRule
        };
        foreach (var (block, line) in rows)
        {
            lines.Add($"- {block}: {line}");
        }

        return string.Join('\n', lines);
    }

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

        var key = FilterDefinition.CanonicalPresetId(libraryTag);
        return key.Length > 0 && Rules.TryGetValue(key, out var rule) ? rule : null;
    }

    /// <summary>
    /// Gets the AI rule brief text for a library tag.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Rule brief or empty string.</returns>
    public static string GetBrief(string? libraryTag)
        => GetByLibraryTag(libraryTag)?.Brief ?? string.Empty;

    /// <summary>
    /// Per-channel daypart target copy for master-clock entertainment presets.
    /// </summary>
    public static string? GetDaypartGuide(string? libraryTag)
        => GetByLibraryTag(libraryTag)?.DaypartGuide;

    /// <summary>
    /// Per-channel daypart target copy from a channel's filter tag.
    /// </summary>
    public static string? GetDaypartGuide(Channel channel)
        => GetDaypartGuide(ExtractLibraryTag(channel.FilterJson));

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

        var key = FilterDefinition.CanonicalPresetId(libraryTag);
        return key.Length > 0 && DefaultPlayoutTemplates.TryGetValue(key, out var templateId) ? templateId : null;
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

        var key = FilterDefinition.CanonicalPresetId(libraryTag);
        return key.Length > 0 && YearConstraints.TryGetValue(key, out var constraints) ? constraints : null;
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

        var key = FilterDefinition.CanonicalPresetId(libraryTag);
        return key.Length > 0 && GenreConstraints.TryGetValue(key, out var constraints) ? constraints : null;
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

        var key = FilterDefinition.CanonicalPresetId(libraryTag);
        return key.Length > 0 && LibraryConstraints.TryGetValue(key, out var constraints) ? constraints : null;
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
    /// Extracts the first ChannelFlow preset id from channel filter JSON.
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
        => FilterDefinition.PresetIdsEqual(libraryTag, "channelflow-news")
            || FilterDefinition.PresetIdsEqual(libraryTag, "channelflow-live-news");

    /// <summary>
    /// Optional max official rating from a preset filter (for example OpenSwim TV-PG).
    /// </summary>
    public static string? GetPresetMaxRating(string? libraryTag)
    {
        var preset = ChannelPresets.Find(libraryTag ?? string.Empty);
        return FilterDefinition.Parse(preset?.FilterJson)?.MaxRating;
    }
}

/// <summary>
/// AI rule metadata for a channel preset.
/// </summary>
public class ChannelAiRuleDefinition
{
    public ChannelAiRuleDefinition(string brief, ChannelCatalogMode defaultCatalogMode, string? daypartGuide = null)
    {
        Brief = brief;
        DefaultCatalogMode = defaultCatalogMode;
        DaypartGuide = daypartGuide;
    }

    public string Brief { get; }

    public ChannelCatalogMode DefaultCatalogMode { get; }

    public string? DaypartGuide { get; }
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

    /// <summary>
    /// Plot keywords that require a supporting genre when the item does not match <see cref="RequiredGenreKeywords"/>.
    /// </summary>
    public IReadOnlyList<string> RestrictedPlotKeywords { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Genres that satisfy a restricted plot-only match (for example CIA mentions on thrillers).
    /// </summary>
    public IReadOnlyList<string> PlotMatchSupportingGenreKeywords { get; set; } = Array.Empty<string>();

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

        if (RequiredPlotKeywords.Count > 0 && TryMatchPlotOrTitle(item, out var matchedKeyword))
        {
            var matchedRequiredGenre = RequiredGenreKeywords.Count > 0
                && genres.Any(genre => RequiredGenreKeywords.Any(keyword =>
                    genre.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

            if (matchedRequiredGenre)
            {
                return true;
            }

            if (RestrictedPlotKeywords.Count > 0
                && matchedKeyword is not null
                && RestrictedPlotKeywords.Any(keyword =>
                    string.Equals(keyword, matchedKeyword, StringComparison.OrdinalIgnoreCase)))
            {
                return PlotMatchSupportingGenreKeywords.Count > 0
                    && genres.Any(genre => PlotMatchSupportingGenreKeywords.Any(keyword =>
                        genre.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            }

            return true;
        }

        return false;
    }

    private bool TryMatchPlotOrTitle(BaseItem item, out string? matchedKeyword)
    {
        matchedKeyword = null;
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
        matchedKeyword = RequiredPlotKeywords.FirstOrDefault(keyword => ContainsPlotKeyword(blob, keyword));
        return matchedKeyword is not null;
    }

    private static bool ContainsPlotKeyword(string blob, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        if (keyword.Contains(' ', StringComparison.Ordinal))
        {
            return blob.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(keyword)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(blob, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// Jellyfin library folder limits for channel catalog and AI manifests.
/// </summary>
public class ChannelCatalogLibraryConstraints
{
    public string LibraryName { get; set; } = string.Empty;

    public string[] AlternateLibraryNames { get; set; } = [];

    public IReadOnlyList<string> AllLibraryNames()
        => new[] { LibraryName }
            .Concat(AlternateLibraryNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
