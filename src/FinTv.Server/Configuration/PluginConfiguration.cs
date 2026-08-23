using System.Text.Json.Serialization;
using FinTv.Domain;

namespace FinTv.Configuration;

public class PluginConfiguration
{
    public string ScheduleTimeZone { get; set; } = "America/New_York";

    public bool DebugLogging { get; set; }

    public string? CommercialLibraryId { get; set; }

    public string? CommercialLibraryTag { get; set; } = "channelflow-commercial";

    public int PlayoutDaysToBuild { get; set; } = 14;

    public int HistoryDaysToConsider { get; set; } = 7;

    [JsonPropertyName("publicBaseUrl")]
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Shared secret for the Jellyfin plugin and IPTV <c>?apiKey=</c> URLs.
    /// Generated at startup when missing and edited on the General page.
    /// </summary>
    public string? ApiKey { get; set; }

    public EbsBackgroundMusicSource EbsBackgroundMusicSource { get; set; } = EbsBackgroundMusicSource.NamedLibrary;

    public string EbsBackgroundMusicLibraryName { get; set; } = "Background Music";

    public string? EbsBackgroundMusicLibraryId { get; set; }

    public EbsSlateVariant EbsSlateVariant { get; set; } = EbsSlateVariant.Usa;

    public EbsDisplayMode EbsDisplayMode { get; set; } = EbsDisplayMode.SlateImage;

    public EbsAudioMode EbsAudioMode { get; set; } = EbsAudioMode.BackgroundMusic;

    public bool AutoRegisterLiveTv { get; set; }

    public string Binarygeek119LogoSetUrl { get; set; } =
        "https://github.com/FlowMeadow01/ChannelFlow-logo";

    public string WeatherStarBaseUrl { get; set; } = "http://127.0.0.1:8080";

    public string WeatherStarPermalinkQuery { get; set; } =
        "hazards=true&current-weather=true&latest-observations=true&hourly=true&hourly-graph=true&travel=true&regional-forecast=true&local-forecast=true&extended-forecast=true&almanac=true&spc-outlook=true&radar=true&stickyKiosk=true&customTextEnable=false&speed=1.00&viewMode=standard&units=us&customText=&mediaVolume=0.75&wide=false&portrait=false&enhanced=false&scanLines=false";

    public string? WeatherDefaultLocationQuery { get; set; }

    /// <summary>
    /// <c>auto</c>, <c>us</c> (NOAA), or <c>world</c> (Open-Meteo).
    /// </summary>
    public string WeatherSource { get; set; } = "auto";

    public bool AutoStartPlaywrightDockerSidecar { get; set; }

    public bool AutoStartWeatherStarDocker { get; set; } = true;

    public bool WeatherStarAutoWideForSixteenNine { get; set; } = true;

    public string? WeatherMusicLibraryId { get; set; }

    public string WeatherMusicLibraryName { get; set; } = "Background Music";

    /// <summary>
    /// How weather alerts appear on non-weather channels: <c>off</c>, <c>cutin</c>, or <c>ticker</c>.
    /// </summary>
    public string WeatherAlertOverlayMode { get; set; } = "off";

    /// <summary>
    /// Minutes between WeatherStar alert-screen cut-ins on other channels.
    /// </summary>
    public int WeatherAlertCutInIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Seconds to show the WeatherStar alerts screen during a cut-in.
    /// </summary>
    public int WeatherAlertCutInDurationSeconds { get; set; } = 20;

    public BlackframeTaskState BlackframeTaskState { get; set; } = new();

    public CommercialBrainzSettings CommercialBrainz { get; set; } = new();

    public List<CommercialSearchPlaylist> CommercialSearchPlaylists { get; set; } = new();

    public JellyfinLibrarySettings JellyfinLibraries { get; set; } = new();

    public CatalogCleanupSettings CatalogCleanup { get; set; } = new();

    public AiSettings Ai { get; set; } = new();

    public List<Guid> AiPendingAutoApplyChannelIds { get; set; } = new();

    public AiGenerateAllJobState AiGenerateAllJob { get; set; } = new();

    public Ws4kpDockerSettings Ws4kp { get; set; } = new();

    public Ws3kpDockerSettings Ws3kp { get; set; } = new();

    /// <summary>
    /// Active WeatherStar engine. Only one of WS4000 or WS3000 runs at a time.
    /// </summary>
    public string WeatherStarVariant { get; set; } = "ws4kp";

    /// <summary>
    /// Live MPEG-TS ffmpeg encode settings. Empty values follow <c>FFMPEG_*</c> environment defaults.
    /// </summary>
    public TranscodeSettings Transcode { get; set; } = new();
}

public class TranscodeSettings
{
    /// <summary>
    /// <c>none</c>, <c>vaapi</c>, or <c>nvenc</c>. Empty means follow <c>FFMPEG_HWACCEL</c>.
    /// </summary>
    public string? HardwareAcceleration { get; set; }

    /// <summary>
    /// Encoder name such as <c>libx264</c>, <c>h264_vaapi</c>, or <c>h264_nvenc</c>. Empty means auto.
    /// </summary>
    public string? VideoEncoder { get; set; }

    /// <summary>
    /// VAAPI render node, typically <c>/dev/dri/renderD128</c>. Empty means follow <c>FFMPEG_VAAPI_DEVICE</c>.
    /// </summary>
    public string? VaapiDevice { get; set; }
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
    public bool Enabled { get; set; }

    public AiProvider DefaultProvider { get; set; } = AiProvider.OpenAi;

    public string? OpenAiApiKey { get; set; }

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public string? VeniceApiKey { get; set; }

    public string VeniceModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Voice id for AI text-to-speech (OpenAI <c>nova</c>, Venice <c>af_sky</c>, etc.).
    /// </summary>
    public string TtsVoice { get; set; } = "nova";

    public int MaxCatalogItemsInPrompt { get; set; } = 250;

    public bool AutoApplyOnChannelAdd { get; set; }

    public bool AutoApplyToAllChannelsOnSave { get; set; }
}

public class CatalogCleanupSettings
{
    /// <summary>
    /// Days a catalog row stays marked missing before the cleanup task deletes it.
    /// </summary>
    public int GracePeriodDays { get; set; } = 7;

    public DateTime? LastCatalogSyncStartedAt { get; set; }

    public DateTime? LastCatalogSyncCompletedAt { get; set; }

    public CatalogCleanupTaskState TaskState { get; set; } = new();

    public CatalogLocalScanTaskState LocalScan { get; set; } = new();
}

public class CatalogCleanupTaskState
{
    public bool IsRunning { get; set; }

    public int MarkedMissing { get; set; }

    public int Removed { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

public class CatalogLocalScanTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public int Found { get; set; }

    public int MarkedMissing { get; set; }

    public int Restored { get; set; }

    public int Skipped { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

public class BlackframeTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastStartedAt { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

public class AiGenerateAllJobState
{
    public bool IsRunning { get; set; }

    public int TotalDays { get; set; }

    public int TotalChannels { get; set; }

    public int TotalSteps { get; set; }

    public int CompletedSteps { get; set; }

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

    public DateTime? LastProgressAt { get; set; }

    public bool WasStale { get; set; }
}

/// <summary>
/// Which Jellyfin libraries ChannelFlow should use for TV, movies, music, and music videos.
/// Empty lists mean every matching library.
/// </summary>
public class JellyfinLibrarySettings
{
    public List<Guid> TvLibraryIds { get; set; } = new();

    public List<Guid> MovieLibraryIds { get; set; } = new();

    public List<Guid> MusicLibraryIds { get; set; } = new();

    public List<Guid> MusicVideoLibraryIds { get; set; } = new();

    public List<Guid> HomeVideoLibraryIds { get; set; } = new();

    /// <summary>
    /// Jellyfin libraries reported by the plugin (name, id, and type).
    /// </summary>
    public List<JellyfinLibraryInfo> Libraries { get; set; } = new();

    public bool Allows(BaseItemKind kind, Guid? libraryId)
    {
        if (kind is BaseItemKind.Folder or BaseItemKind.Playlist)
        {
            return true;
        }

        var selected = kind switch
        {
            BaseItemKind.Series or BaseItemKind.Episode or BaseItemKind.Season => TvLibraryIds,
            BaseItemKind.Movie => MovieLibraryIds,
            BaseItemKind.Audio => MusicLibraryIds,
            BaseItemKind.MusicVideo => MusicVideoLibraryIds,
            BaseItemKind.Video => HomeVideoLibraryIds,
            _ => null
        };

        if (selected is not { Count: > 0 })
        {
            return true;
        }

        return libraryId is Guid id && selected.Contains(id);
    }

    public static List<Guid> Normalize(IEnumerable<Guid>? ids)
        => ids?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList()
            ?? new List<Guid>();
}

/// <summary>
/// A Jellyfin media library reported by the plugin.
/// </summary>
public class JellyfinLibraryInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }
}

public class WeatherGuideSlotCache
{
    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? Description { get; set; }

    public List<string> Categories { get; set; } = new();

    public string? ForecastDate { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
