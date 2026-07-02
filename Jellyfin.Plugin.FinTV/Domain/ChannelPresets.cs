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
    /// Category-based subchannels (1.1, 1.2, 3.1, 4.2, etc.).
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
        Preset(119, 1.1m, "FlashBack TV", ChannelContentType.TvShow, "TV Shows", "1970's - 2000's TV shows and movies", "fintv-flashback", "Shows/FlashBack_TV.png"),
        Preset(120, 1.2m, "Retro TV", ChannelContentType.TvShow, "TV Shows", "1910's - 1960's TV shows and movies", "fintv-retro", "Shows/Retro_TV.png"),
        Preset(121, 1.3m, "[Open Swim]", ChannelContentType.TvShow, "TV Shows", "Cartoons and kid shows and movies", "fintv-open-swim", "Shows/[open_swim].png"),
        Preset(122, 1.4m, "Flip Television", ChannelContentType.TvShow, "TV Shows", "Reality TV", "fintv-reality", "Shows/Flip_Television.png"),
        Preset(123, 1.5m, "BinaryGeek119 News & Weather", ChannelContentType.Weather, "TV Shows", "News and weather channel", "fintv-news-weather", "Weather/WeatherStar4000.png", weather: true),
        Preset(124, 1.6m, "Past Tense News", ChannelContentType.TvShow, "TV Shows", "Retro news", "fintv-past-tense-news", logoPath: null, useLogo: false),
        Preset(125, 1.7m, "Cops And Robbers", ChannelContentType.TvShow, "TV Shows", "Crime themed channel", "fintv-crime", "Shows/cops_and_robbers.png"),
        Preset(126, 5.1m, "WeatherStar4000", ChannelContentType.Weather, "Weather", "Live local weather via WeatherStar 4000", "fintv-weatherstar4000", "Weather/WeatherStar4000.png", weather: true),
        Preset(128, 1.8m, "Slappy", ChannelContentType.TvShow, "TV Shows", "Comedy themed channel", "fintv-comedy", "Shows/Slappy.png"),
        Preset(129, 1.9m, "Winning", ChannelContentType.TvShow, "TV Shows", "Game shows channel", "fintv-game-shows", "Shows/winning.png"),
        Preset(130, 2.1m, "GET LEARNEDED", ChannelContentType.TvShow, "TV Shows", "Educational TV shows and movies", "fintv-education", "Shows/GET_LEARNEDED.png"),
        Preset(133, 2.2m, "YouTube TV", ChannelContentType.TvShow, "TV Shows", "YouTube channels", "fintv-youtube", logoPath: null, useLogo: false),
        Preset(203, 3.1m, "Creature Double Feature", ChannelContentType.Movie, "Movies", "Creature and monster movies", "fintv-creature", "Movies/Creature_Double_Feature.png"),
        Preset(204, 3.2m, "Hero TV", ChannelContentType.Movie, "Movies", "Super hero movies", "fintv-hero", "Movies/Hero_TV.png"),
        Preset(205, 3.3m, "That's Funny", ChannelContentType.Movie, "Movies", "Comedian movies and TV shows", "fintv-funny", logoPath: null, useLogo: false),
        Preset(207, 3.4m, "The Holiday Channel", ChannelContentType.Movie, "Movies", "Holiday themed TV shows and movies", "fintv-holiday", "The Holiday Channel/christmas-marry.png"),
        Preset(312, 4.1m, "The Parody Channel", ChannelContentType.MusicVideo, "Music Videos", "Parody music videos", "fintv-parody", "Music Videos Channels/The-Parody-Channel.png"),
        Preset(313, 4.2m, "Rap On Tap", ChannelContentType.MusicVideo, "Music Videos", "Rap and hip hop music videos", "fintv-rap", "Music Videos Channels/Rap-On-Tap.png"),
        Preset(314, 4.3m, "HeadPhone Jack", ChannelContentType.MusicVideo, "Music Videos", "All other music videos", "fintv-music-video", "Music Videos Channels/HeadPhone_Jack.png"),
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
        bool weather = false)
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
            UseBinarygeek119Logo = useLogo && !string.IsNullOrWhiteSpace(logoPath),
            FilterJson = JsonSerializer.Serialize(new { tags = new[] { libraryTag } }),
            IsWeatherChannel = weather
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

    /// <summary>
    /// Resolves the channel number for the selected numbering mode.
    /// </summary>
    /// <param name="mode">Numbering mode.</param>
    /// <returns>The channel number to create.</returns>
    public decimal GetNumber(ChannelPresetNumberingMode mode)
        => mode == ChannelPresetNumberingMode.Subchannels ? SubchannelNumber : LegacyNumber;
}
