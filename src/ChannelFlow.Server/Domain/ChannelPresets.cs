namespace FinTv.Domain;

/// <summary>
/// How ready-made channel numbers are assigned when presets are applied.
/// </summary>
public enum ChannelPresetNumberingMode
{
    /// <summary>
    /// Original Binarygeek119 whole-number channels (119, 120, 203, etc.).
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Legacy-major subchannels (119.1, 124.2, 126.3, 203.1, 312.3, etc.).
    /// </summary>
    Subchannels = 1
}

/// <summary>
/// Built-in Binarygeek119 channel lineup presets.
/// </summary>
public static class ChannelPresets
{
    public const string Binarygeek119LogoSetName = "Binarygeek119 Set";

    /// <summary>
    /// Gets all ready-made channel definitions.
    /// </summary>
    public static IReadOnlyList<ChannelPresetDefinition> All { get; } =
    [
        Preset(119, 119.1m, "FlashBack TV", ChannelContentType.TvShow, "TV Shows", "1970–2009 TV and movies (first-episode year for series)", "channelflow-flashback", "Shows/FlashBack_TV.png", catalogMode: ChannelCatalogMode.Mixed, minYear: 1970, maxYear: 2009),
        Preset(120, 119.2m, "Retro TV", ChannelContentType.TvShow, "TV Shows", "1910–1969 TV and movies (first-episode year for series)", "channelflow-retro", "Shows/Retro_TV.png", catalogMode: ChannelCatalogMode.Mixed, minYear: 1910, maxYear: 1969),
        Preset(121, 119.3m, "[OpenSwim]", ChannelContentType.TvShow, "TV Shows", "Nick, Disney, Fox Kids, and Cartoon Network style kids TV/movies; any year; TV-PG max", "channelflow-open-swim", "Shows/[open_swim].png", catalogMode: ChannelCatalogMode.Mixed, maxRating: "TV-PG"),
        Preset(122, 119.4m, "Flip Television", ChannelContentType.TvShow, "TV Shows", "Reality TV themed shows and movies", "channelflow-reality", "Shows/Flip_Television.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(124, 119.6m, "WeatherStar4000", ChannelContentType.Weather, "TV Shows", "Live WeatherStar 4000+ MPEG-TS weather channel", "channelflow-weatherstar4000", "Weather/WeatherStar4000.png", weather: true),
        Preset(124.2m, 119.7m, "WeatherStar3000", ChannelContentType.Weather, "TV Shows", "Live WeatherStar 3000+ MPEG-TS weather channel", "channelflow-weatherstar3000", "Weather/WeatherStar3000.png", weather: true),
        Preset(125, 124.1m, "Past Tense News", ChannelContentType.TvShow, "TV Shows", "Home movies treated as live breaking news", "channelflow-past-tense-news", "News/Past_Tense_News.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(123.1m, 119.8m, "FlowWire News", ChannelContentType.News, "TV Shows", "Live FlowWire RSS news channel with optional TTS", "channelflow-live-news", "News/FlowWire.png"),
        Preset(128, 124.2m, "Cops And Robbers", ChannelContentType.TvShow, "TV Shows", "Crime and cop themed TV shows and movies (genre or plot)", "channelflow-crime", "Shows/cops_and_robbers.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(129, 124.3m, "Slappy", ChannelContentType.TvShow, "TV Shows", "Fox network clone: comedy TV and movies with Friday 5–8pm Slappy's Toon Takeover", "channelflow-comedy", "Shows/Slappy.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(130, 126.1m, "Winning", ChannelContentType.TvShow, "TV Shows", "Game shows channel", "channelflow-game-shows", "Shows/winning.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(133, 126.2m, "GET LEARNEDED", ChannelContentType.TvShow, "TV Shows", "Educational tv shows and movies", "channelflow-education", "Shows/GET_LEARNEDED.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(134, 126.3m, "YouTube TV", ChannelContentType.TvShow, "TV Shows", "Content from Jellyfin TV library YouTube only", "channelflow-youtube", "Shows/YouTube_TV.png"),
        Preset(203, 203.1m, "Creature Double Feature", ChannelContentType.Movie, "Movies", "Creature and monster movies and TV (genre, plot, or tags)", "channelflow-creature", "Movies/Creature_Double_Feature.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(204, 203.2m, "Hero TV", ChannelContentType.Movie, "Movies", "Anyone who saves or protects people — heroes, rescuers, and champions", "channelflow-hero", "Movies/Hero_TV.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(205, 203.3m, "That's Funny", ChannelContentType.Movie, "Movies", "Stand-up comedies from Stand-Up Comedies Movies and Stand-Up Comedies TV Shows", "channelflow-funny", "Movies/That's_Funny.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(207, 203.4m, "The Holiday Channel", ChannelContentType.Movie, "Movies", "Seasonal holiday TV and movies; off-season plays The Holiday Channel.mkv", "channelflow-holiday", "The_Holiday_Channel/The Holiday Channel-plane.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(312, 312.1m, "The Parody Channel", ChannelContentType.MusicVideo, "Music Videos", "Parody music videos", "channelflow-parody", "Music Videos Channels/The-Parody-Channel.png"),
        Preset(313, 312.2m, "Rap On Tap", ChannelContentType.MusicVideo, "Music Videos", "Rap and hip hop music videos", "channelflow-rap", "Music Videos Channels/Rap-On-Tap.png"),
        Preset(314, 312.3m, "HeadPhone Jack", ChannelContentType.MusicVideo, "Music Videos", "All other music videos", "channelflow-music-video", "Music Videos Channels/HeadPhone_Jack.png"),
    ];

    /// <summary>
    /// Finds a preset by stable identifier.
    /// </summary>
    /// <param name="id">Preset identifier.</param>
    /// <returns>The preset, if found.</returns>
    public static ChannelPresetDefinition? Find(string id)
        => All.FirstOrDefault(p => FilterDefinition.PresetIdsEqual(p.Id, id));

    private static ChannelPresetDefinition Preset(
        decimal legacyNumber,
        decimal subchannelNumber,
        string name,
        ChannelContentType contentType,
        string category,
        string description,
        string libraryTag,
        string? logoPath,
        bool useLogo = true,
        bool weather = false,
        ChannelCatalogMode? catalogMode = null,
        int? minYear = null,
        int? maxYear = null,
        string? maxRating = null)
    {
        return new ChannelPresetDefinition
        {
            Id = libraryTag,
            LegacyNumber = legacyNumber,
            SubchannelNumber = subchannelNumber,
            Name = name,
            ContentType = contentType,
            Category = category,
            Description = description,
            LibraryTag = libraryTag,
            LogoRelativePath = logoPath,
            UseBinarygeek119Logo = useLogo,
            FilterJson = BuildFilterJson(libraryTag, minYear, maxYear, maxRating),
            IsWeatherChannel = weather,
            CatalogMode = catalogMode ?? ChannelAiRules.GetByLibraryTag(libraryTag)?.DefaultCatalogMode
        };
    }

    private static string BuildFilterJson(string libraryTag, int? minYear, int? maxYear, string? maxRating)
    {
        var filter = new Dictionary<string, object?> { ["presetId"] = libraryTag };
        if (minYear.HasValue)
        {
            filter["minYear"] = minYear.Value;
        }

        if (maxYear.HasValue)
        {
            filter["maxYear"] = maxYear.Value;
        }

        if (!string.IsNullOrWhiteSpace(maxRating))
        {
            filter["maxRating"] = maxRating;
        }

        return FinTvJson.Serialize(filter);
    }
}

/// <summary>
/// Ready-made channel template metadata.
/// </summary>
public class ChannelPresetDefinition
{
    public string Id { get; set; } = string.Empty;

    public decimal LegacyNumber { get; set; }

    public decimal SubchannelNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public ChannelContentType ContentType { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string LibraryTag { get; set; } = string.Empty;

    public string? LogoRelativePath { get; set; }

    public bool UseBinarygeek119Logo { get; set; }

    public string? FilterJson { get; set; }

    public bool IsWeatherChannel { get; set; }

    public ChannelCatalogMode? CatalogMode { get; set; }

    /// <summary>
    /// Resolves the channel number for the selected numbering mode.
    /// </summary>
    /// <param name="mode">Numbering mode.</param>
    /// <returns>The channel number to create.</returns>
    public decimal GetNumber(ChannelPresetNumberingMode mode)
        => mode == ChannelPresetNumberingMode.Subchannels ? SubchannelNumber : LegacyNumber;
}
