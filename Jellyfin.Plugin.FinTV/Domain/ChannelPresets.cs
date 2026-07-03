using System.Text.Json;

namespace Jellyfin.Plugin.FinTV.Domain;

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
        Preset(119, 119.1m, "FlashBack TV", ChannelContentType.TvShow, "TV Shows", "1970–2010 TV and movies (first-episode year for series)", "fintv-flashback", "Shows/FlashBack_TV.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(120, 119.2m, "Retro TV", ChannelContentType.TvShow, "TV Shows", "1910–1969 TV and movies (first-episode year for series)", "fintv-retro", "Shows/Retro_TV.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(121, 119.3m, "[OpenSwim]", ChannelContentType.TvShow, "TV Shows", "Kids and teen shows and movies only", "fintv-open-swim", "Shows/[open_swim].png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(122, 119.4m, "Flip Television", ChannelContentType.TvShow, "TV Shows", "Reality TV themed shows and movies", "fintv-reality", "Shows/Flip_Television.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(123, 119.5m, "BinaryGeek119 News", ChannelContentType.TvShow, "TV Shows", "News", "fintv-news", logoPath: null),
        Preset(124, 119.6m, "WeatherStar4000", ChannelContentType.Weather, "TV Shows", "Weather channel", "fintv-weatherstar4000", "Weather/WeatherStar4000.png", weather: true),
        Preset(125, 124.1m, "Past Tense News", ChannelContentType.TvShow, "TV Shows", "Content from Jellyfin library Past Tense News only", "fintv-past-tense-news", logoPath: null),
        Preset(128, 124.2m, "Cops And Robbers", ChannelContentType.TvShow, "TV Shows", "Crime and cop themed TV shows only", "fintv-crime", "Shows/cops_and_robbers.png"),
        Preset(129, 124.3m, "Slappy", ChannelContentType.TvShow, "TV Shows", "Comedy TV and movies with 6pm Animation Domination block", "fintv-comedy", "Shows/Slappy.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(130, 126.1m, "Winning", ChannelContentType.TvShow, "TV Shows", "Game shows channel", "fintv-game-shows", "Shows/winning.png"),
        Preset(133, 126.2m, "GET LEARNEDED", ChannelContentType.TvShow, "TV Shows", "Educational tv shows and movies", "fintv-education", "Shows/GET_LEARNEDED.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(134, 126.3m, "YouTube TV", ChannelContentType.TvShow, "TV Shows", "Youtube channels", "fintv-youtube", logoPath: null),
        Preset(203, 203.1m, "Creature Double Feature", ChannelContentType.Movie, "Movies", "Creature, monster movies", "fintv-creature", "Movies/Creature_Double_Feature.png", catalogMode: ChannelCatalogMode.MovieOnly),
        Preset(204, 203.2m, "Hero TV", ChannelContentType.Movie, "Movies", "Super hero movies", "fintv-hero", "Movies/Hero_TV.png", catalogMode: ChannelCatalogMode.MovieOnly),
        Preset(205, 203.3m, "That's Funny", ChannelContentType.Movie, "Movies", "Comedian movies and tv shows", "fintv-funny", logoPath: null, catalogMode: ChannelCatalogMode.Mixed),
        Preset(207, 203.4m, "The Holiday Channel", ChannelContentType.Movie, "Movies", "Seasonal holiday TV and movies; off-season plays The Holiday Channel.mkv", "fintv-holiday", "The Holiday Channel/The Holiday Channel-plane.png", catalogMode: ChannelCatalogMode.Mixed),
        Preset(312, 312.1m, "The Parody Channel", ChannelContentType.MusicVideo, "Music Videos", "Parody music videos", "fintv-parody", "Music Videos Channels/The-Parody-Channel.png"),
        Preset(313, 312.2m, "Rap On Tap", ChannelContentType.MusicVideo, "Music Videos", "Rap and hip hop music videos", "fintv-rap", "Music Videos Channels/Rap-On-Tap.png"),
        Preset(314, 312.3m, "HeadPhone Jack", ChannelContentType.MusicVideo, "Music Videos", "All other music videos", "fintv-music-video", "Music Videos Channels/HeadPhone_Jack.png"),
    ];

    /// <summary>
    /// Finds a preset by stable identifier.
    /// </summary>
    /// <param name="id">Preset identifier.</param>
    /// <returns>The preset, if found.</returns>
    public static ChannelPresetDefinition? Find(string id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

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
        ChannelCatalogMode? catalogMode = null)
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
            FilterJson = JsonSerializer.Serialize(new { tags = new[] { libraryTag } }),
            IsWeatherChannel = weather,
            CatalogMode = catalogMode ?? ChannelAiRules.GetByLibraryTag(libraryTag)?.DefaultCatalogMode
        };
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
