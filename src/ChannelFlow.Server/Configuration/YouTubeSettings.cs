namespace FinTv.Configuration;

public class YouTubeSettings
{
    public static readonly string[] KnownCategories =
    [
        "sponsor",
        "selfpromo",
        "interaction",
        "intro",
        "outro",
        "preview",
        "hook",
        "filler",
        "music_offtopic"
    ];

    public static readonly string[] DefaultCategories =
    [
        "sponsor",
        "selfpromo",
        "interaction",
        "intro",
        "outro",
        "preview"
    ];

    /// <summary>
    /// Prefer TV/Android player clients and higher-quality formats when cookies are present.
    /// A YouTube Premium account cookie can unlock Premium formats; ChannelFlow cannot enable Premium itself.
    /// </summary>
    public bool PreferPremium { get; set; } = true;

    public bool SponsorBlockEnabled { get; set; } = true;

    public List<string> SponsorBlockCategories { get; set; } = new(DefaultCategories);

    /// <summary>
    /// Netscape cookies.txt backup in AppSettings JSON. The live file is
    /// <c>youtube-cookies.txt</c> in the config folder. Never returned by the API.
    /// </summary>
    public string? NetscapeCookies { get; set; }

    public DateTime? CookiesSavedAtUtc { get; set; }

    public static List<string> NormalizeCategories(IEnumerable<string>? categories)
    {
        var selected = (categories ?? [])
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value) && KnownCategories.Contains(value))
            .Cast<string>()
            .Distinct()
            .ToList();

        return selected.Count > 0 ? selected : new List<string>(DefaultCategories);
    }
}
