using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Resolves seasonal state, logos, and offline media for The Holiday Channel.
/// </summary>
public class HolidayChannelService
{
    private readonly ILibraryManager _libraryManager;

    public HolidayChannelService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public bool IsHolidayChannel(Channel channel)
        => HolidayChannelCalendar.IsHolidayChannel(channel);

    public HolidayDefinition? GetActiveHoliday(DateOnly date)
        => HolidayChannelCalendar.GetActiveHoliday(date);

    public HolidayDefinition? GetActiveOrUpcomingHoliday(DateOnly date)
        => HolidayChannelCalendar.GetActiveOrUpcomingHoliday(date);

    public DateOnly GetScheduleDateUtc(DateTime utcNow)
    {
        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
    }

    public string? ResolveEffectiveLogoPath(Channel channel, DateOnly date)
    {
        if (!IsHolidayChannel(channel))
        {
            return channel.ChannelLogoPath;
        }

        var holiday = GetActiveHoliday(date);
        var relativePath = HolidayChannelCalendar.GetLogoRelativePath(holiday, date);
        return ResolveLogoPathFromRelative(relativePath);
    }

    public string? ResolveEffectiveLogoFileName(Channel channel, DateOnly date)
    {
        if (!IsHolidayChannel(channel))
        {
            return channel.LogoFileName;
        }

        var path = ResolveEffectiveLogoPath(channel, date);
        return string.IsNullOrWhiteSpace(path)
            ? Path.GetFileName(HolidayChannelCalendar.OffSeasonLogoRelativePath)
            : Path.GetFileName(path);
    }

    public BaseItem? FindOfflineMediaItem()
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Video },
            Name = HolidayChannelCalendar.OffSeasonMediaTitle
        };

        return _libraryManager.GetItemsResult(query).Items
            .FirstOrDefault(item => item.Name.Equals(HolidayChannelCalendar.OffSeasonMediaTitle, StringComparison.OrdinalIgnoreCase));
    }

    public bool MatchesActiveHoliday(BaseItem item, Channel channel, DateOnly scheduleDate)
    {
        if (!IsHolidayChannel(channel))
        {
            return true;
        }

        var holiday = GetActiveHoliday(scheduleDate);
        return holiday is not null && HolidayChannelCalendar.MatchesHolidayContent(item, holiday);
    }

    public string? ResolveLogoPathFromRelative(string relativePath)
        => LogoSetService.ResolveBinarygeek119File(relativePath);

    public string? ResolveOffSeasonVideoPath()
        => LogoSetService.ResolveBinarygeek119File(HolidayChannelCalendar.OffSeasonVideoRelativePath);
}
