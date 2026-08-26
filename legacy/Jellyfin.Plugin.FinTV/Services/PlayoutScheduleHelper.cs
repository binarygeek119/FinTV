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

    /// <summary>
    /// Analyzes how much future playout exists relative to the configured horizon.
    /// </summary>
    public static PlayoutHorizonStatus AnalyzeHorizon(DateTime nowUtc, DateTime? latestFinishUtc)
    {
        var horizonEnd = GetHorizonEndUtc(nowUtc);
        var targetDays = GetPlayoutDaysToBuild();

        if (!latestFinishUtc.HasValue || latestFinishUtc.Value <= nowUtc)
        {
            return new PlayoutHorizonStatus(
                IsAtHorizon: false,
                NeedsOneDayExtension: false,
                NeedsFullBuild: true,
                HorizonEndUtc: horizonEnd,
                LatestFinishUtc: latestFinishUtc,
                GapToHorizon: horizonEnd - nowUtc);
        }

        if (latestFinishUtc.Value >= horizonEnd)
        {
            return new PlayoutHorizonStatus(
                IsAtHorizon: true,
                NeedsOneDayExtension: false,
                NeedsFullBuild: false,
                HorizonEndUtc: horizonEnd,
                LatestFinishUtc: latestFinishUtc,
                GapToHorizon: TimeSpan.Zero);
        }

        var gap = horizonEnd - latestFinishUtc.Value;
        var coverage = latestFinishUtc.Value - nowUtc;
        var needsOneDay = gap > TimeSpan.Zero
            && gap <= TimeSpan.FromHours(26)
            && coverage >= TimeSpan.FromDays(targetDays - 1).Subtract(TimeSpan.FromHours(2));

        return new PlayoutHorizonStatus(
            IsAtHorizon: false,
            NeedsOneDayExtension: needsOneDay,
            NeedsFullBuild: !needsOneDay,
            HorizonEndUtc: horizonEnd,
            LatestFinishUtc: latestFinishUtc,
            GapToHorizon: gap);
    }
}

public readonly record struct PlayoutHorizonStatus(
    bool IsAtHorizon,
    bool NeedsOneDayExtension,
    bool NeedsFullBuild,
    DateTime HorizonEndUtc,
    DateTime? LatestFinishUtc,
    TimeSpan GapToHorizon);
