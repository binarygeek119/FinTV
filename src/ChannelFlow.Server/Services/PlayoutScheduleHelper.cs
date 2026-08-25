using FinTv.Configuration;

namespace FinTv.Services;

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
        var days = FinTvRuntime.Current?.Configuration.PlayoutDaysToBuild ?? MaxPlayoutDays;
        return Math.Clamp(days, 1, MaxPlayoutDays);
    }

    public static DateTime GetHorizonEndUtc(DateTime? fromUtc = null)
        => GetScheduleDayStartUtc(fromUtc ?? DateTime.UtcNow, GetPlayoutDaysToBuild());

    /// <summary>
    /// Midnight of the schedule-time-zone calendar day that contains <paramref name="utc"/>,
    /// plus <paramref name="dayOffset"/> local calendar days.
    /// Using UTC <see cref="DateTime.Date"/> is wrong after evening in US time zones:
    /// it jumps to the next UTC day and rebuilds from 7–8 PM local.
    /// </summary>
    public static DateTime GetScheduleDayStartUtc(DateTime utc, int dayOffset = 0, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
        var startLocal = DateTime.SpecifyKind(
            new DateTime(local.Year, local.Month, local.Day, 0, 0, 0).AddDays(dayOffset),
            DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(startLocal, timeZone);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.ConvertTimeToUtc(startLocal.AddHours(1), timeZone);
        }
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
