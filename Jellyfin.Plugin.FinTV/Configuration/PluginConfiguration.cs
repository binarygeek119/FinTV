using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinTV.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ScheduleTimeZone { get; set; } = "America/New_York";

    public string? CommercialLibraryId { get; set; }

    public string? CommercialLibraryTag { get; set; } = "fintv-commercial";

    public int PlayoutDaysToBuild { get; set; } = 3;

    public int HistoryDaysToConsider { get; set; } = 7;

    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Where EBS off-air background music is selected from.
    /// </summary>
    public EbsBackgroundMusicSource EbsBackgroundMusicSource { get; set; } = EbsBackgroundMusicSource.NamedLibrary;

    /// <summary>
    /// Music library name used when <see cref="EbsBackgroundMusicSource"/> is <see cref="EbsBackgroundMusicSource.NamedLibrary"/>.
    /// </summary>
    public string EbsBackgroundMusicLibraryName { get; set; } = "Background Music";

    /// <summary>
    /// Optional Jellyfin library identifier for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryId { get; set; }

    /// <summary>
    /// Which bundled off-air slate artwork to show when a channel has no scheduled media.
    /// </summary>
    public EbsSlateVariant EbsSlateVariant { get; set; } = EbsSlateVariant.Usa;

    /// <summary>
    /// What viewers see during off-air playback and stream errors.
    /// </summary>
    public EbsDisplayMode EbsDisplayMode { get; set; } = EbsDisplayMode.SlateImage;

    /// <summary>
    /// Audio track paired with off-air video.
    /// </summary>
    public EbsAudioMode EbsAudioMode { get; set; } = EbsAudioMode.BackgroundMusic;

    public bool AutoRegisterLiveTv { get; set; }

    public string Binarygeek119LogoSetUrl { get; set; } =
        "https://github.com/binarygeek119/open-channel-logos/tree/fintv2";

    /// <summary>
    /// Base URL for the WeatherStar 4000 page. FinTV appends lat/lon query parameters per channel.
    /// </summary>
    public string WeatherStarBaseUrl { get; set; } = "https://weather.jmthornton.net";

    public BlackframeTaskState BlackframeTaskState { get; set; } = new();

    public CommercialBrainzSettings CommercialBrainz { get; set; } = new();

    public AiSettings Ai { get; set; } = new();
}

public class AiSettings
{
    /// <summary>
    /// Master switch — when false, no LLM calls or lineup generation.
    /// </summary>
    public bool Enabled { get; set; }

    public AiProvider DefaultProvider { get; set; } = AiProvider.OpenAi;

    public string? OpenAiApiKey { get; set; }

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public string? VeniceApiKey { get; set; }

    public string VeniceModel { get; set; } = "gpt-4o-mini";

    public int MaxCatalogItemsInPrompt { get; set; } = 250;
}

public class BlackframeTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}
