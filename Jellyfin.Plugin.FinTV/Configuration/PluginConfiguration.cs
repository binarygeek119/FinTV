using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinTV.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ScheduleTimeZone { get; set; } = "America/New_York";

    public string? CommercialLibraryId { get; set; }

    public string? CommercialLibraryTag { get; set; } = "fintv-commercial";

    public int PlayoutDaysToBuild { get; set; } = 14;

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
    /// Base URL for the WeatherStar page (scheme + host + port only). Display settings come from <see cref="WeatherStarPermalinkQuery"/>.
    /// </summary>
    public string WeatherStarBaseUrl { get; set; } = "https://weather.jmthornton.net";

    /// <summary>
    /// ws4kp permalink query string copied from WeatherStar (display toggles, units, speed, etc.).
    /// FinTV merges each channel's <c>latLonQuery</c>, forces <c>kiosk=true</c>, and optionally sets <c>wide</c> at capture time.
    /// </summary>
    public string WeatherStarPermalinkQuery { get; set; } = WeatherStarChannelService.DefaultWeatherStarPermalinkQuery;

    /// <summary>
    /// When true, start the Playwright Chromium Docker CDP sidecar during Jellyfin startup.
    /// </summary>
    public bool AutoStartPlaywrightDockerSidecar { get; set; }

    /// <summary>
    /// When true, start the self-hosted WeatherStar Docker container during Jellyfin startup.
    /// Uses ws4kp or ws3kp based on <see cref="WeatherStarBaseUrl"/>; defaults to ws4kp when the URL is not local.
    /// </summary>
    public bool AutoStartWeatherStarDocker { get; set; }

    /// <summary>
    /// When true, weather capture sets <c>wide=true</c> for 16:9 channels and <c>wide=false</c> for 4:3 channels.
    /// </summary>
    public bool WeatherStarAutoWideForSixteenNine { get; set; } = true;

    public BlackframeTaskState BlackframeTaskState { get; set; } = new();

    public CommercialBrainzSettings CommercialBrainz { get; set; } = new();

    public AiSettings Ai { get; set; } = new();

    /// <summary>
    /// Channel IDs waiting for AI auto-apply (lineup + 14-day playout rebuild).
    /// </summary>
    public List<Guid> AiPendingAutoApplyChannelIds { get; set; } = new();

    /// <summary>
    /// Progress for background staggered AI generate-all jobs.
    /// </summary>
    public AiGenerateAllJobState AiGenerateAllJob { get; set; } = new();

    public Ws4kpDockerSettings Ws4kp { get; set; } = new();

    public Ws3kpDockerSettings Ws3kp { get; set; } = new();
}

public class Ws4kpDockerSettings : IWeatherStarDockerSettings
{
    public int HostPort { get; set; } = 8080;

    public string Image { get; set; } = "ghcr.io/netbymatt/ws4kp";
}

public class Ws3kpDockerSettings : IWeatherStarDockerSettings
{
    public int HostPort { get; set; } = 8083;

    public string Image { get; set; } = "ghcr.io/netbymatt/ws3kp";
}

public interface IWeatherStarDockerSettings
{
    int HostPort { get; set; }

    string Image { get; set; }
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

    /// <summary>
    /// When true, new channels get default AI settings, generated lineups, and a 14-day playout rebuild.
    /// </summary>
    public bool AutoApplyOnChannelAdd { get; set; }

    /// <summary>
    /// When true, saving AI settings also generates and applies lineups for all eligible channels.
    /// </summary>
    public bool AutoApplyToAllChannelsOnSave { get; set; }
}

public class BlackframeTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

/// <summary>
/// Progress for staggered AI generate-all (one channel, one day at a time).
/// </summary>
public class AiGenerateAllJobState
{
    public bool IsRunning { get; set; }

    public int TotalDays { get; set; }

    public int TotalChannels { get; set; }

    public int TotalSteps { get; set; }

    public int CompletedSteps { get; set; }

    /// <summary>Current day being built (1-based for display).</summary>
    public int CurrentDay { get; set; }

    public string? CurrentChannelName { get; set; }

    public int LineupsGenerated { get; set; }

    public int LineupsFailed { get; set; }

    public int PlayoutDaysBuilt { get; set; }

    public int PlayoutDaysFailed { get; set; }

    public string? LastError { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool WasCancelled { get; set; }
}
