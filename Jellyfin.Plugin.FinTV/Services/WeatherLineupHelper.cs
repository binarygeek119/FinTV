using Jellyfin.Plugin.FinTV.Domain;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Shared weather-channel lineup and playout hour blocks (24 one-hour slots per day).
/// </summary>
public static class WeatherLineupHelper
{
    public const int HoursPerDay = 24;

    public const int HalfHourSlotsPerHour = 2;

    public static IReadOnlyList<LineupSlot> CreateDailySlots()
    {
        return Enumerable.Range(0, HoursPerDay)
            .Select(hour => new LineupSlot
            {
                SlotIndex = hour * HalfHourSlotsPerHour,
                SpanSlots = HalfHourSlotsPerHour,
                Candidates = new List<SlotCandidate>()
            })
            .ToList();
    }

    public static IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> BuildHourBlocksUtc(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        TimeZoneInfo scheduleTimeZone)
    {
        var blocks = new List<(DateTime StartUtc, DateTime EndUtc)>();
        if (rangeEndUtc <= rangeStartUtc)
        {
            return blocks;
        }

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(rangeStartUtc, scheduleTimeZone);
        var cursorLocal = new DateTime(
            localStart.Year,
            localStart.Month,
            localStart.Day,
            localStart.Hour,
            0,
            0,
            DateTimeKind.Unspecified);

        if (cursorLocal < localStart)
        {
            cursorLocal = cursorLocal.AddHours(1);
        }

        while (true)
        {
            var blockStartUtc = TimeZoneInfo.ConvertTimeToUtc(cursorLocal, scheduleTimeZone);
            var blockEndLocal = cursorLocal.AddHours(1);
            var blockEndUtc = TimeZoneInfo.ConvertTimeToUtc(blockEndLocal, scheduleTimeZone);

            if (blockStartUtc >= rangeEndUtc)
            {
                break;
            }

            if (blockEndUtc > rangeEndUtc)
            {
                blockEndUtc = rangeEndUtc;
            }

            if (blockEndUtc <= blockStartUtc)
            {
                break;
            }

            blocks.Add((blockStartUtc, blockEndUtc));
            cursorLocal = blockEndLocal;
        }

        return blocks;
    }

    public static string FormatHourTitle(DateTime hourStartUtc, TimeZoneInfo scheduleTimeZone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(hourStartUtc, scheduleTimeZone);
        return $"Local Weather · {local:h:mm tt}";
    }

    public static TimeZoneInfo GetScheduleTimeZone()
        => ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
}
