using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// JSON TV guide for the ChannelFlow Web UI.
/// </summary>
[ApiController]
[Route("api/guide")]
[Authorize(Policy = "admin")]
public class GuideController : ControllerBase
{
    private readonly EpgService _epg;
    private readonly IPublicBaseUrl _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideController"/> class.
    /// </summary>
    /// <param name="epg">EPG service.</param>
    /// <param name="appHost">Public base URL helper.</param>
    public GuideController(EpgService epg, IPublicBaseUrl appHost)
    {
        _epg = epg;
        _appHost = appHost;
    }

    /// <summary>
    /// Gets a channel/time TV guide built from current playout.
    /// </summary>
    /// <param name="from">Window start in UTC. Defaults to 30 minutes before the current half-hour.</param>
    /// <param name="date">Calendar date (yyyy-MM-dd) in the schedule time zone. Ignored when <paramref name="from"/> is set.</param>
    /// <param name="hours">Window length in hours (1–24). Defaults to 6.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Guide channels and programmes.</returns>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<TvGuidePage>> Get(
        [FromQuery] DateTime? from,
        [FromQuery] string? date,
        [FromQuery] int hours = 6,
        CancellationToken cancellationToken = default)
    {
        hours = Math.Clamp(hours, 1, 24);
        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        var fromUtc = from.HasValue
            ? ToUtc(from.Value)
            : FromDateOrDefault(date, tz);
        var toUtc = fromUtc.AddHours(hours);
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        return await _epg.GetGuideAsync(fromUtc, toUtc, baseUrl, cancellationToken);
    }

    private static DateTime FromDateOrDefault(string? date, TimeZoneInfo tz)
    {
        if (DateTime.TryParseExact(
                date,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var day))
        {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            if (day.Date != localNow.Date)
            {
                var prime = new DateTime(day.Year, day.Month, day.Day, 19, 0, 0);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(prime, DateTimeKind.Unspecified), tz);
            }
        }

        return DefaultWindowStartUtc(tz);
    }

    private static DateTime DefaultWindowStartUtc(TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var snapped = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute < 30 ? 0 : 30, 0);
        snapped = snapped.AddMinutes(-30);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(snapped, DateTimeKind.Unspecified), tz);
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
