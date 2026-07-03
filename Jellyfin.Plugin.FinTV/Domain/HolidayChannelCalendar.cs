namespace Jellyfin.Plugin.FinTV.Domain;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

/// <summary>
/// Seasonal windows and theme matching for The Holiday Channel (fintv-holiday).
/// </summary>
public static class HolidayChannelCalendar
{
    public const string LibraryTag = "fintv-holiday";

    public const string OffSeasonLogoRelativePath = "The Holiday Channel/The Holiday Channel-plane.png";

    public const string OffSeasonMediaTitle = "The Holiday Channel";

    public const int MaxDaysBeforeObservance = 30;

    private static readonly HolidayDefinition[] Holidays =
    [
        new(
            "valentines",
            "Valentine's Day",
            observanceMonth: 2,
            observanceDay: 14,
            daysBefore: 30,
            logoPath: "The Holiday Channel/The Holiday Channel-Valentine's Day.png",
            matchKeywords: ["valentine", "valentines", "sweetheart", "cupid"],
            seasonMonths: [2]),
        new(
            "easter",
            "Easter",
            dateKind: HolidayDateKind.EasterSunday,
            daysBefore: 30,
            logoPath: "The Holiday Channel/easter.png",
            matchKeywords: ["easter", "bunny", "rabbit", "egg hunt", "resurrection", "paschal"],
            seasonMonths: [3, 4]),
        new(
            "independence",
            "Independence Day",
            observanceMonth: 7,
            observanceDay: 4,
            daysBefore: 30,
            bleedIntoPriorMonth: true,
            logoPath: "The Holiday Channel/The Holiday Channel - Independence Day.png",
            matchKeywords: ["independence day", "july 4", "july fourth", "fourth of july", "4th of july", "fireworks", "patriotic"],
            seasonMonths: [6, 7]),
        new(
            "halloween",
            "Halloween",
            observanceMonth: 10,
            observanceDay: 31,
            daysBefore: 30,
            logoPath: "The Holiday Channel/halloween_alt-happy.png",
            earlySeasonLogoPath: "The Holiday Channel/October.png",
            earlySeasonLogoUntilDay: 15,
            matchKeywords: ["halloween", "spooky", "ghost", "witch", "pumpkin", "haunted", "october", "trick or treat", "horror"],
            seasonMonths: [10]),
        new(
            "thanksgiving",
            "Thanksgiving",
            dateKind: HolidayDateKind.UsThanksgiving,
            daysBefore: 30,
            logoPath: "The Holiday Channel/The Holiday Channel -Thanksgiving Day.png",
            matchKeywords: ["thanksgiving", "turkey", "pilgrim", "harvest feast"],
            seasonMonths: [11]),
        new(
            "christmas",
            "Christmas",
            observanceMonth: 12,
            observanceDay: 25,
            daysBefore: 54,
            bleedIntoPriorMonth: true,
            logoPath: "The Holiday Channel/christmas-marry.png",
            matchKeywords: ["christmas", "xmas", "santa", "nutcracker", "grinch", "snowman", "krampus", "noel", "nativity"],
            seasonMonths: [11, 12])
    ];

    /// <summary>
    /// All configured holidays (more can be added when logos are ready).
    /// </summary>
    public static IReadOnlyList<HolidayDefinition> All => Holidays;

    public static bool IsHolidayChannel(Channel channel)
        => string.Equals(ChannelAiRules.ExtractLibraryTag(channel.FilterJson), LibraryTag, StringComparison.OrdinalIgnoreCase);

    public static HolidayDefinition? GetActiveHoliday(DateOnly date)
    {
        HolidayDefinition? best = null;
        var bestDistance = int.MaxValue;

        foreach (var holiday in Holidays)
        {
            foreach (var year in new[] { date.Year - 1, date.Year, date.Year + 1 })
            {
                var observance = GetObservanceDate(holiday, year);
                if (!TryGetWindow(holiday, observance, out var start, out var end))
                {
                    continue;
                }

                if (date < start || date > end)
                {
                    continue;
                }

                var distance = Math.Abs(date.DayNumber - observance.DayNumber);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = holiday;
                }
            }
        }

        return best;
    }

    public static HolidayDefinition? GetActiveOrUpcomingHoliday(DateOnly date, int maxDaysBefore = MaxDaysBeforeObservance)
    {
        var active = GetActiveHoliday(date);
        if (active is not null)
        {
            return active;
        }

        HolidayDefinition? next = null;
        var nextDistance = int.MaxValue;

        foreach (var holiday in Holidays)
        {
            foreach (var year in new[] { date.Year, date.Year + 1 })
            {
                var observance = GetObservanceDate(holiday, year);
                if (observance < date)
                {
                    continue;
                }

                var distance = observance.DayNumber - date.DayNumber;
                if (distance > maxDaysBefore)
                {
                    continue;
                }

                if (distance < nextDistance)
                {
                    nextDistance = distance;
                    next = holiday;
                }
            }
        }

        return next;
    }

    public static string GetLogoRelativePath(HolidayDefinition? holiday, DateOnly date)
    {
        if (holiday is null)
        {
            return OffSeasonLogoRelativePath;
        }

        if (!string.IsNullOrWhiteSpace(holiday.EarlySeasonLogoPath)
            && holiday.EarlySeasonLogoUntilDay.HasValue
            && date.Month == GetObservanceDate(holiday, date.Year).Month
            && date.Day <= holiday.EarlySeasonLogoUntilDay.Value)
        {
            return holiday.EarlySeasonLogoPath;
        }

        return holiday.LogoRelativePath;
    }

    public static bool MatchesHolidayContent(BaseItem item, HolidayDefinition holiday)
    {
        if (item is Episode episode)
        {
            return episode.Series is not null && MatchesHolidayContent(episode.Series, holiday);
        }

        var searchable = new List<string> { item.Name ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            searchable.Add(item.Overview);
        }

        if (item.Tags is { Length: > 0 })
        {
            searchable.AddRange(item.Tags);
        }

        if (item.Genres is { Length: > 0 })
        {
            searchable.AddRange(item.Genres);
        }

        var blob = string.Join(' ', searchable);
        if (holiday.MatchKeywords.Any(keyword =>
                blob.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var month = GetPremiereMonth(item);
        return month.HasValue && holiday.SeasonMonths.Contains(month.Value);
    }

    public static DateOnly GetObservanceDate(HolidayDefinition holiday, int year)
    {
        return holiday.DateKind switch
        {
            HolidayDateKind.EasterSunday => ComputeEasterSunday(year),
            HolidayDateKind.UsThanksgiving => ComputeUsThanksgiving(year),
            _ => new DateOnly(year, holiday.ObservanceMonth, holiday.ObservanceDay)
        };
    }

    private static bool TryGetWindow(HolidayDefinition holiday, DateOnly observance, out DateOnly start, out DateOnly end)
    {
        start = observance.AddDays(-Math.Min(holiday.DaysBefore, MaxDaysBeforeObservance));
        end = observance.AddDays(holiday.DaysAfter);

        if (holiday.BleedIntoPriorMonth)
        {
            var priorMonthStart = new DateOnly(observance.Year, observance.Month, 1).AddMonths(-1);
            if (start > priorMonthStart)
            {
                start = priorMonthStart;
            }
        }

        return start <= end;
    }

    private static int? GetPremiereMonth(BaseItem item)
    {
        if (item.PremiereDate.HasValue)
        {
            return item.PremiereDate.Value.Month;
        }

        return null;
    }

    private static DateOnly ComputeEasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static DateOnly ComputeUsThanksgiving(int year)
    {
        var date = new DateOnly(year, 11, 1);
        while (date.DayOfWeek != DayOfWeek.Thursday)
        {
            date = date.AddDays(1);
        }

        return date.AddDays(21);
    }
}

public enum HolidayDateKind
{
    Fixed = 0,
    EasterSunday = 1,
    UsThanksgiving = 2
}

public sealed class HolidayDefinition
{
    public HolidayDefinition(
        string id,
        string name,
        int daysBefore,
        string logoPath,
        string[] matchKeywords,
        int[] seasonMonths,
        int observanceMonth = 1,
        int observanceDay = 1,
        HolidayDateKind dateKind = HolidayDateKind.Fixed,
        int daysAfter = 0,
        bool bleedIntoPriorMonth = false,
        string? earlySeasonLogoPath = null,
        int? earlySeasonLogoUntilDay = null)
    {
        Id = id;
        Name = name;
        DaysBefore = daysBefore;
        DaysAfter = daysAfter;
        LogoRelativePath = logoPath;
        MatchKeywords = matchKeywords;
        SeasonMonths = seasonMonths;
        ObservanceMonth = observanceMonth;
        ObservanceDay = observanceDay;
        DateKind = dateKind;
        BleedIntoPriorMonth = bleedIntoPriorMonth;
        EarlySeasonLogoPath = earlySeasonLogoPath;
        EarlySeasonLogoUntilDay = earlySeasonLogoUntilDay;
    }

    public string Id { get; }

    public string Name { get; }

    public int DaysBefore { get; }

    public int DaysAfter { get; }

    public bool BleedIntoPriorMonth { get; }

    public string LogoRelativePath { get; }

    public string? EarlySeasonLogoPath { get; }

    public int? EarlySeasonLogoUntilDay { get; }

    public string[] MatchKeywords { get; }

    public int[] SeasonMonths { get; }

    public int ObservanceMonth { get; }

    public int ObservanceDay { get; }

    public HolidayDateKind DateKind { get; }
}
