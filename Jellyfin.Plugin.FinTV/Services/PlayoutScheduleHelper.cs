using Jellyfin.Plugin.FinTV.Configuration;

namespace Jellyfin.Plugin.FinTV.Services;

public enum PlayoutBuildMode
{
    ReplaceWindow = 0,
    ExtendHorizon = 1
}

public static class PlayoutScheduleHelper
{
    public const int MaxPlayoutDays = 14;

    public static int GetPlayoutDaysToBuild()
    {
        var days = Plugin.Instance?.Configuration.PlayoutDaysToBuild ?? MaxPlayoutDays;
        return Math.Clamp(days, 1, MaxPlayoutDays);
    }

    public static DateTime GetHorizonEndUtc(DateTime? fromUtc = null)
    {
        var start = (fromUtc ?? DateTime.UtcNow).Date;
        return start.AddDays(GetPlayoutDaysToBuild());
    }
}
