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
    /// <param name="from">Window start in UTC. When set, <paramref name="hours"/> is applied from this instant.</param>
    /// <param name="date">Calendar date (yyyy-MM-dd) in the schedule time zone. Ignored when <paramref name="from"/> is set. Defaults to today as a full local day.</param>
    /// <param name="hours">Window length in hours (1–24) when <paramref name="from"/> is set. Ignored for calendar-date queries.</param>
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
        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        DateTime fromUtc;
        DateTime toUtc;
        if (from.HasValue)
        {
            hours = Math.Clamp(hours, 1, 24);
            fromUtc = ToUtc(from.Value);
            toUtc = fromUtc.AddHours(hours);
        }
        else
        {
            fromUtc = LocalDayStartUtc(date, tz, out toUtc);
        }

        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        return await _epg.GetGuideAsync(fromUtc, toUtc, baseUrl, cancellationToken);
    }

    private static DateTime LocalDayStartUtc(string? date, TimeZoneInfo tz, out DateTime toUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var day = localNow;
        if (DateTime.TryParseExact(
                date,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            day = parsed;
        }

        var startLocal = DateTime.SpecifyKind(
            new DateTime(day.Year, day.Month, day.Day, 0, 0, 0),
            DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1);
        toUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
        return TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
    }

    /// <summary>
    /// Serves a library poster for the TV Guide programme details modal.
    /// </summary>
    [HttpGet("poster/{itemId:guid}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> GetPoster(
        Guid itemId,
        [FromServices] GuideMetadataService guideMetadata,
        CancellationToken cancellationToken)
    {
        var path = await guideMetadata.GetOrFetchPosterImagePathAsync(itemId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, GetPosterContentType(path));
    }

    private static string GetPosterContentType(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
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
