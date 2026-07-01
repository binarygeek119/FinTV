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

    public bool AutoRegisterLiveTv { get; set; }

    public string Binarygeek119LogoSetUrl { get; set; } =
        "https://github.com/binarygeek119/open-channel-logos/tree/master/Binarygeek119%20Set";

    public BlackframeTaskState BlackframeTaskState { get; set; } = new();
}

public class BlackframeTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}
