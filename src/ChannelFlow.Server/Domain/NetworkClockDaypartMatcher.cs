namespace FinTv.Domain;

/// <summary>
/// Scores catalog titles against network-clock dayparts so kids cartoons
/// do not land in Late Night and Prime Time can prefer movies.
/// </summary>
public static class NetworkClockDaypartMatcher
{
    public const int HardReject = -10_000;

    public static bool IsRerunDaypartName(string? daypartName)
        => !string.IsNullOrWhiteSpace(daypartName)
            && daypartName.Contains("rerun", StringComparison.OrdinalIgnoreCase);

    public static int MaxSeriesEpisodes(string? daypartName)
    {
        var key = Normalize(daypartName);
        if (key.Contains("toon takeover"))
        {
            return 4;
        }

        if (key.Contains("prime") || key.Contains("mid-day") || key.Contains("midday"))
        {
            return 2;
        }

        return 2;
    }

    public static bool PrefersMovies(string? libraryTag, string? daypartName)
    {
        var taste = ResolveTaste(libraryTag, daypartName);
        return taste?.PreferMovies == true;
    }

    public static int Score(
        string? title,
        string? type,
        IReadOnlyList<string>? genres,
        string? officialRating,
        int? year,
        string? plot,
        string? libraryTag,
        string? daypartName)
    {
        if (IsRerunDaypartName(daypartName) || string.IsNullOrWhiteSpace(daypartName))
        {
            return 0;
        }

        var genreList = genres ?? Array.Empty<string>();
        var blob = BuildBlob(title, plot, genreList);
        var animation = IsAnimation(genreList, blob);
        var kids = IsKids(officialRating, genreList, animation, blob);
        var adult = IsAdult(officialRating);
        var movie = IsMovie(type);
        var taste = ResolveTaste(libraryTag, daypartName);
        var key = Normalize(daypartName);

        if (taste?.ForbidKids == true || key.Contains("late night") || key.Contains("adult"))
        {
            if (kids)
            {
                return HardReject;
            }
        }

        if (taste?.ForbidAdult == true
            || key.Contains("before school")
            || key.Contains("after school")
            || (key.Contains("morning") && !key.Contains("late")))
        {
            if (adult)
            {
                return HardReject;
            }
        }

        if (key.Contains("toon takeover") && (!animation || adult))
        {
            return HardReject;
        }

        if (taste is null)
        {
            return 0;
        }

        var score = 0;
        if (taste.PreferMovies)
        {
            score += movie ? 24 : -16;
        }
        else if (movie)
        {
            score -= 4;
        }

        if (taste.PreferAnimation)
        {
            score += animation ? 20 : -12;
        }
        else if (taste.PreferLiveAction && animation)
        {
            score -= 12;
        }

        score += CountHits(blob, taste.PreferKeywords) * 8;
        score += CountHits(genreList, taste.PreferGenres) * 6;
        score -= CountHits(blob, taste.AvoidKeywords) * 10;

        if (taste.YearMin is int min && taste.YearMax is int max && year is int y)
        {
            score += y >= min && y <= max ? 10 : -Math.Min(12, Math.Abs(y - ((min + max) / 2)) / 4);
        }

        if (kids && taste.ForbidKids)
        {
            return HardReject;
        }

        return score;
    }

    public static bool IsHardReject(int score) => score <= HardReject / 2;

    private static DaypartTaste? ResolveTaste(string? libraryTag, string? daypartName)
    {
        var tag = FilterDefinition.CanonicalPresetId(libraryTag ?? string.Empty);
        if (tag.Length == 0 || string.IsNullOrWhiteSpace(daypartName))
        {
            return GenericTaste(daypartName);
        }

        var key = DaypartKey(daypartName);
        if (Tastes.TryGetValue(tag, out var byDaypart) && byDaypart.TryGetValue(key, out var taste))
        {
            return taste;
        }

        return GenericTaste(daypartName);
    }

    private static DaypartTaste? GenericTaste(string? daypartName)
    {
        var key = DaypartKey(daypartName);
        return key switch
        {
            "late-night" => new DaypartTaste { ForbidKids = true },
            "before-school" or "after-school" or "morning" => new DaypartTaste { ForbidAdult = true },
            "toon-takeover" => new DaypartTaste { PreferAnimation = true, ForbidAdult = true, ForbidKids = false },
            _ => null
        };
    }

    private static string DaypartKey(string? daypartName)
    {
        var key = Normalize(daypartName);
        if (key.Contains("toon takeover"))
        {
            return "toon-takeover";
        }

        if (key.Contains("late night") || key.Contains("adult"))
        {
            return "late-night";
        }

        if (key.Contains("early bird") || key.Contains("rerun"))
        {
            return "early-bird";
        }

        if (key.Contains("before school"))
        {
            return "before-school";
        }

        if (key.Contains("after school"))
        {
            return "after-school";
        }

        if (key.Contains("teen"))
        {
            return "teen-hour";
        }

        if (key.Contains("prime"))
        {
            return "prime-time";
        }

        if (key.Contains("mid-day") || key.Contains("midday") || key.Contains("mid day"))
        {
            return "mid-day";
        }

        if (key.Contains("morning"))
        {
            return "morning";
        }

        return key;
    }

    private static readonly Dictionary<string, Dictionary<string, DaypartTaste>> Tastes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelflow-flashback"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["before-school"] = KidsCartoons(1978, 1992, "saturday", "short", "cartoon"),
            ["morning"] = Sitcoms(1970, 1989, "sitcom", "family", "variety"),
            ["mid-day"] = Drama(1988, 2001, "soap", "drama", "daytime"),
            ["after-school"] = KidsCartoons(1990, 2009, "cartoon", "animated", "toon"),
            ["teen-hour"] = Teen(1988, 2002, "teen", "coming of age", "high school"),
            ["prime-time"] = Movies(1970, 2000, "blockbuster", "feature"),
            ["late-night"] = CultNight()
        },
        ["channelflow-retro"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["before-school"] = KidsCartoons(1910, 1965, "popeye", "betty boop", "short"),
            ["morning"] = Sitcoms(1930, 1969, "variety", "vaudeville"),
            ["mid-day"] = Sitcoms(1930, 1965, "sitcom", "radio", "play"),
            ["after-school"] = KidsCartoons(1950, 1969, "cartoon", "animated"),
            ["teen-hour"] = Teen(1950, 1969, "youth", "teen", "beach"),
            ["prime-time"] = Movies(1910, 1969, "hollywood", "feature", "classic"),
            ["late-night"] = new DaypartTaste
            {
                PreferMovies = true,
                ForbidKids = true,
                PreferKeywords = ["noir", "thriller", "mystery", "horror", "avant"],
                PreferGenres = ["Thriller", "Mystery", "Crime", "Horror", "Film-Noir"],
                YearMin = 1910,
                YearMax = 1969
            }
        },
        ["channelflow-reality"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["before-school"] = Clips("competition", "clip", "challenge"),
            ["morning"] = Lifestyle("home", "cooking", "makeover", "garden"),
            ["mid-day"] = Lifestyle("dating", "lifestyle", "vlog", "housewife"),
            ["after-school"] = Lifestyle("casual", "light", "family"),
            ["teen-hour"] = Teen(1998, 2026, "influencer", "teen", "competition"),
            ["prime-time"] = new DaypartTaste
            {
                PreferMovies = false,
                PreferKeywords = ["survival", "elimination", "contest", "tribe", "voted off"],
                PreferGenres = ["Reality"],
                ForbidKids = true
            },
            ["late-night"] = new DaypartTaste
            {
                ForbidKids = true,
                PreferKeywords = ["reunion", "after dark", "uncensored", "behind the scenes"],
                PreferGenres = ["Reality"]
            }
        },
        ["channelflow-crime"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["before-school"] = new DaypartTaste
            {
                ForbidAdult = true,
                PreferKeywords = ["mystery", "detective", "clue"],
                PreferGenres = ["Mystery", "Family"]
            },
            ["morning"] = new DaypartTaste
            {
                ForbidAdult = true,
                PreferKeywords = ["whodunnit", "mystery", "detective"],
                PreferGenres = ["Mystery", "Crime"]
            },
            ["mid-day"] = new DaypartTaste
            {
                PreferKeywords = ["procedural", "forensic", "police", "detective"],
                PreferGenres = ["Crime", "Drama"],
                YearMin = 1980,
                YearMax = 2026
            },
            ["after-school"] = KidsCartoons(1980, 2015, "mystery", "detective", "solve"),
            ["teen-hour"] = Teen(1990, 2026, "teen", "detective", "high school"),
            ["prime-time"] = new DaypartTaste
            {
                PreferKeywords = ["crime", "forensic", "gritty", "homicide"],
                PreferGenres = ["Crime", "Drama", "Thriller"],
                ForbidKids = true
            },
            ["late-night"] = new DaypartTaste
            {
                ForbidKids = true,
                PreferMovies = false,
                PreferKeywords = ["bodycam", "unsolved", "cold case", "true crime"],
                PreferGenres = ["Crime", "Documentary", "Thriller"]
            }
        },
        ["channelflow-comedy"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["before-school"] = KidsCartoons(1930, 2000, "slapstick", "short", "looney"),
            ["morning"] = Sitcoms(1970, 2010, "stand-up", "sketch", "family"),
            ["mid-day"] = Sitcoms(1975, 2015, "sitcom", "marathon"),
            ["after-school"] = KidsCartoons(1985, 2015, "cartoon", "comedy"),
            ["teen-hour"] = Teen(1990, 2015, "teen", "sitcom", "sketch"),
            ["prime-time"] = new DaypartTaste
            {
                ForbidKids = true,
                PreferKeywords = ["roast", "sketch", "uncensored", "stand-up"],
                PreferGenres = ["Comedy"],
                AvoidKeywords = ["preschool", "nick jr"]
            },
            ["toon-takeover"] = KidsCartoons(1985, 2020, "cartoon", "toon", "animated"),
            ["late-night"] = new DaypartTaste
            {
                ForbidKids = true,
                PreferKeywords = ["stand-up", "uncensored", "surreal", "adult"],
                PreferGenres = ["Comedy"]
            }
        },
        ["channelflow-education"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["before-school"] = KidsCartoons(1980, 2026, "fact", "short", "learn"),
            ["morning"] = new DaypartTaste
            {
                ForbidAdult = true,
                PreferKeywords = ["science", "kids", "how to", "tutorial"],
                PreferGenres = ["Documentary", "Science", "Family"]
            },
            ["mid-day"] = new DaypartTaste
            {
                PreferKeywords = ["nature", "history", "wildlife", "earth"],
                PreferGenres = ["Documentary", "History", "Nature"]
            },
            ["after-school"] = KidsCartoons(1985, 2026, "educational", "learn", "science"),
            ["teen-hour"] = new DaypartTaste
            {
                PreferKeywords = ["philosophy", "study", "lecture", "advanced"],
                PreferGenres = ["Documentary", "Educational"]
            },
            ["prime-time"] = new DaypartTaste
            {
                PreferKeywords = ["university", "lecture", "essay", "deep"],
                PreferGenres = ["Documentary", "Educational"]
            },
            ["late-night"] = new DaypartTaste
            {
                ForbidKids = true,
                PreferKeywords = ["sociology", "history", "theory", "dark"],
                PreferGenres = ["Documentary", "History"]
            }
        }
    };

    private static DaypartTaste KidsCartoons(int yearMin, int yearMax, params string[] keywords)
        => new()
        {
            PreferAnimation = true,
            ForbidAdult = true,
            PreferKeywords = keywords,
            PreferGenres = ["Animation", "Animated", "Cartoon", "Kids", "Family", "Children"],
            YearMin = yearMin,
            YearMax = yearMax
        };

    private static DaypartTaste Sitcoms(int yearMin, int yearMax, params string[] keywords)
        => new()
        {
            PreferLiveAction = true,
            ForbidAdult = true,
            PreferKeywords = keywords,
            PreferGenres = ["Comedy", "Sitcom", "Family"],
            AvoidKeywords = ["preschool", "nick jr"],
            YearMin = yearMin,
            YearMax = yearMax
        };

    private static DaypartTaste Drama(int yearMin, int yearMax, params string[] keywords)
        => new()
        {
            PreferLiveAction = true,
            PreferKeywords = keywords,
            PreferGenres = ["Drama", "Soap", "Romance"],
            AvoidKeywords = ["cartoon", "preschool"],
            YearMin = yearMin,
            YearMax = yearMax
        };

    private static DaypartTaste Teen(int yearMin, int yearMax, params string[] keywords)
        => new()
        {
            PreferKeywords = keywords,
            PreferGenres = ["Drama", "Teen", "Comedy"],
            AvoidKeywords = ["preschool", "toddler"],
            ForbidKids = false,
            YearMin = yearMin,
            YearMax = yearMax
        };

    private static DaypartTaste Movies(int yearMin, int yearMax, params string[] keywords)
        => new()
        {
            PreferMovies = true,
            ForbidKids = true,
            PreferKeywords = keywords,
            PreferGenres = ["Action", "Adventure", "Drama", "Sci-Fi", "Thriller"],
            YearMin = yearMin,
            YearMax = yearMax
        };

    private static DaypartTaste CultNight()
        => new()
        {
            PreferMovies = true,
            ForbidKids = true,
            PreferKeywords = ["cult", "uncut", "lost", "director", "midnight"],
            PreferGenres = ["Horror", "Thriller", "Sci-Fi", "Mystery"],
            AvoidKeywords = ["preschool", "nick jr", "saturday morning"]
        };

    private static DaypartTaste Clips(params string[] keywords)
        => new()
        {
            ForbidAdult = true,
            PreferKeywords = keywords,
            PreferGenres = ["Reality"]
        };

    private static DaypartTaste Lifestyle(params string[] keywords)
        => new()
        {
            PreferKeywords = keywords,
            PreferGenres = ["Reality", "Family"]
        };

    private static bool IsMovie(string? type)
        => string.Equals(type, "Movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Clip", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnimation(IReadOnlyList<string> genres, string blob)
        => genres.Any(g => ContainsAny(g, "Animation", "Animated", "Cartoon", "Anime"))
            || ContainsAny(blob, "animated series", "cartoon", "anime");

    private static bool IsKids(string? rating, IReadOnlyList<string> genres, bool animation, string blob)
    {
        var rank = RatingRank(rating);
        if (rank is 1)
        {
            return true;
        }

        if (genres.Any(g => ContainsAny(g, "Kids", "Children", "Preschool", "Child")))
        {
            return true;
        }

        if (ContainsAny(blob, "preschool", "nick jr", "disney junior", "for children"))
        {
            return true;
        }

        return animation && rank is not > 2;
    }

    private static bool IsAdult(string? rating)
    {
        var rank = RatingRank(rating);
        return rank is >= 5;
    }

    private static int? RatingRank(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        return rating.Trim().ToUpperInvariant() switch
        {
            "TV-Y" => 1,
            "G" or "TV-Y7" or "TV-G" => 2,
            "PG" or "TV-PG" => 3,
            "PG-13" or "TV-14" => 4,
            "R" or "NC-17" or "TV-MA" => 5,
            "UR" or "NR" or "UNRATED" or "NOT RATED" or "NOTRATED" or "N/R" => 5,
            _ => null
        };
    }

    private static string BuildBlob(string? title, string? plot, IReadOnlyList<string> genres)
        => string.Join(' ', new[] { title, plot, string.Join(' ', genres) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static int CountHits(string blob, IReadOnlyList<string> keywords)
        => keywords.Count(keyword => blob.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static int CountHits(IReadOnlyList<string> genres, IReadOnlyList<string> keywords)
        => keywords.Count(keyword => genres.Any(genre => genre.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private sealed class DaypartTaste
    {
        public IReadOnlyList<string> PreferGenres { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> PreferKeywords { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> AvoidKeywords { get; init; } = Array.Empty<string>();

        public int? YearMin { get; init; }

        public int? YearMax { get; init; }

        public bool PreferMovies { get; init; }

        public bool PreferAnimation { get; init; }

        public bool PreferLiveAction { get; init; }

        public bool ForbidKids { get; init; }

        public bool ForbidAdult { get; init; }
    }
}
