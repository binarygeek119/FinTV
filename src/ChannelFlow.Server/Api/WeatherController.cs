using FinTv;
using FinTv.Domain;
using FinTv.Services;
using FinTv.Weather;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/weather")]
[Authorize(Policy = "admin")]
public class WeatherController : ControllerBase
{
    private readonly JellyfinCatalogService _catalog;
    private readonly ChannelService _channels;
    private readonly WeatherAlertOverlayService _alerts;
    private readonly StreamService _streams;

    public WeatherController(
        JellyfinCatalogService catalog,
        ChannelService channels,
        WeatherAlertOverlayService alerts,
        StreamService streams)
    {
        _catalog = catalog;
        _channels = channels;
        _alerts = alerts;
        _streams = streams;
    }

    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var weatherChannels = (await _channels.GetAllAsync(cancellationToken))
            .Where(c => c.ContentType == ChannelContentType.Weather)
            .Select(c => new
            {
                id = c.Id,
                number = ChannelNumbers.Format(c.Number),
                name = c.Name,
                location = c.WeatherLocationQuery,
                zip = WeatherLocationParser.ExtractZip(c.WeatherLocationQuery),
                weatherLocationQuery = c.WeatherLocationQuery
            })
            .ToList();

        object musicLibraries;
        try
        {
            musicLibraries = _catalog.GetMusicLibraries()
                .Select(l => new { id = l.Id, name = l.Name })
                .ToList();
        }
        catch (Exception)
        {
            musicLibraries = Array.Empty<object>();
        }

        return Ok(new
        {
            weatherStarVariant = NormalizeWeatherStarId(config?.WeatherStarVariant),
            weatherSource = string.IsNullOrWhiteSpace(config?.WeatherSource) ? "auto" : config.WeatherSource,
            nativeRenderer = true,
            weatherStarPermalinkQuery = config?.WeatherStarPermalinkQuery,
            weatherStarAutoWideForSixteenNine = config?.WeatherStarAutoWideForSixteenNine ?? true,
            weatherMusicLibraryId = config?.WeatherMusicLibraryId,
            weatherMusicLibraryName = config?.WeatherMusicLibraryName,
            weatherDefaultLocationQuery = string.IsNullOrWhiteSpace(config?.WeatherDefaultLocationQuery)
                ? string.Empty
                : config.WeatherDefaultLocationQuery.Trim(),
            weatherAlertOverlayMode = WeatherAlertOverlayService.FormatMode(
                WeatherAlertOverlayService.ParseMode(config?.WeatherAlertOverlayMode)),
            weatherAlertCutInIntervalMinutes = Math.Clamp(config?.WeatherAlertCutInIntervalMinutes ?? 15, 1, 180),
            weatherAlertCutInDurationSeconds = Math.Clamp(config?.WeatherAlertCutInDurationSeconds ?? 20, 5, 120),
            weatherAlertTestActive = _alerts.HasActiveTest,
            weatherChannels,
            musicLibraries,
            publicSite = false,
            bind = "127.0.0.1"
        });
    }

    [HttpPut("settings")]
    public async Task<ActionResult<object>> UpdateSettings(
        [FromBody] WeatherSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request.WeatherStarPermalinkQuery is not null)
        {
            plugin.Configuration.WeatherStarPermalinkQuery =
                WeatherStarChannelService.NormalizePermalinkQuery(request.WeatherStarPermalinkQuery);
        }

        if (!string.IsNullOrWhiteSpace(request.WeatherStarFullPermalink))
        {
            var split = WeatherStarChannelService.SplitPermalink(request.WeatherStarFullPermalink);
            plugin.Configuration.WeatherStarPermalinkQuery = split.Query;
        }

        if (request.WeatherStarAutoWideForSixteenNine.HasValue)
        {
            plugin.Configuration.WeatherStarAutoWideForSixteenNine = request.WeatherStarAutoWideForSixteenNine.Value;
        }

        if (request.WeatherMusicLibraryId is not null)
        {
            plugin.Configuration.WeatherMusicLibraryId = string.IsNullOrWhiteSpace(request.WeatherMusicLibraryId)
                ? null
                : request.WeatherMusicLibraryId.Trim();
        }

        if (request.WeatherMusicLibraryName is not null)
        {
            plugin.Configuration.WeatherMusicLibraryName = request.WeatherMusicLibraryName.Trim();
        }

        plugin.Configuration.WeatherSource = WeatherDataClient.ParseSource(request.WeatherSource) switch
        {
            WeatherSourceKind.UnitedStates => "us",
            WeatherSourceKind.World => "world",
            _ => "auto"
        };

        var defaultLocation = request.DefaultLocation ?? request.DefaultZip;
        if (defaultLocation is not null)
        {
            if (string.IsNullOrWhiteSpace(defaultLocation))
            {
                plugin.Configuration.WeatherDefaultLocationQuery = null;
            }
            else
            {
                try
                {
                    plugin.Configuration.WeatherDefaultLocationQuery = WeatherLocationParser.NormalizeLocation(defaultLocation);
                }
                catch (ArgumentException ex)
                {
                    return BadRequest(new { message = ex.Message });
                }
            }
        }

        if (request.WeatherAlertOverlayMode is not null)
        {
            plugin.Configuration.WeatherAlertOverlayMode = WeatherAlertOverlayService.FormatMode(
                WeatherAlertOverlayService.ParseMode(request.WeatherAlertOverlayMode));
        }

        if (request.WeatherAlertCutInIntervalMinutes.HasValue)
        {
            plugin.Configuration.WeatherAlertCutInIntervalMinutes =
                Math.Clamp(request.WeatherAlertCutInIntervalMinutes.Value, 1, 180);
        }

        if (request.WeatherAlertCutInDurationSeconds.HasValue)
        {
            plugin.Configuration.WeatherAlertCutInDurationSeconds =
                Math.Clamp(request.WeatherAlertCutInDurationSeconds.Value, 5, 120);
        }

        if (request.Channels is not null)
        {
            foreach (var row in request.Channels)
            {
                try
                {
                    var location = WeatherLocationParser.NormalizeLocation(row.Location ?? row.Zip);
                    var updated = await _channels.UpdateWeatherLocationAsync(row.Id, location, cancellationToken);
                    if (updated is null)
                    {
                        return NotFound(new { message = "Weather channel not found." });
                    }
                }
                catch (ArgumentException ex)
                {
                    return BadRequest(new { message = ex.Message });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.WeatherStarVariant))
        {
            plugin.Configuration.WeatherStarVariant = NormalizeWeatherStarId(request.WeatherStarVariant);
        }

        plugin.SaveConfiguration();
        return await GetStatus(cancellationToken);
    }

    [HttpPost("alerts/test")]
    public async Task<ActionResult<object>> TestAlerts(
        [FromBody] WeatherAlertTestRequest? request,
        CancellationToken cancellationToken)
    {
        var mode = WeatherAlertOverlayService.ParseMode(request?.Mode);
        if (mode is WeatherAlertOverlayMode.Off)
        {
            return BadRequest(new { message = "Pick the alerts screen or scrolling text, then try again." });
        }

        var seconds = Math.Clamp(request?.DurationSeconds ?? 20, 5, 120);
        try
        {
            var preview = await _alerts.StartTestAsync(mode, TimeSpan.FromSeconds(seconds), cancellationToken);
            _streams.PunchInWeatherAlert();
            var first = preview.Alerts.FirstOrDefault();
            var liveHint = mode == WeatherAlertOverlayMode.CutIn
                ? "Live TV, movies, and music switch to ebs_fullscreen.png and keep the show audio ducked underneath. Use Stop test to end it early."
                : "Live TV, movies, and music show the scrolling ebs_strip.png bar. Use Stop test to end it early.";
            return Ok(new
            {
                mode = WeatherAlertOverlayService.FormatMode(preview.Mode),
                durationSeconds = seconds,
                tickerText = preview.TickerText,
                eventName = first?.Event,
                headline = first?.Headline,
                hazardsJpeg = preview.HazardsJpeg,
                tickerPng = preview.TickerPng,
                tickerHasText = preview.TickerHasText,
                message = $"Sample {first?.Event ?? "weather alert"} preview. {liveHint}"
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("alerts/test/stop")]
    public ActionResult<object> StopAlertTest()
    {
        var stopped = _alerts.StopTest();
        _streams.InterruptAllCurrentItems();
        return Ok(new
        {
            stopped,
            message = stopped
                ? "Weather alert test stopped. Live TV is back on the regular lineup."
                : "No weather alert test was running."
        });
    }

    private static string NormalizeWeatherStarId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("3", StringComparison.OrdinalIgnoreCase)
            ? "ws3kp"
            : "ws4kp";
}

public class WeatherSettingsRequest
{
    public string? WeatherStarPermalinkQuery { get; set; }

    public string? WeatherStarFullPermalink { get; set; }

    public bool? WeatherStarAutoWideForSixteenNine { get; set; }

    public string? WeatherStarVariant { get; set; }

    public string? WeatherSource { get; set; }

    public string? WeatherMusicLibraryId { get; set; }

    public string? WeatherMusicLibraryName { get; set; }

    public string? DefaultZip { get; set; }

    public string? DefaultLocation { get; set; }

    public string? WeatherAlertOverlayMode { get; set; }

    public int? WeatherAlertCutInIntervalMinutes { get; set; }

    public int? WeatherAlertCutInDurationSeconds { get; set; }

    public List<WeatherChannelLocationRequest>? Channels { get; set; }
}

public class WeatherAlertTestRequest
{
    public string? Mode { get; set; }

    public int? DurationSeconds { get; set; }
}

public class WeatherChannelLocationRequest
{
    public Guid Id { get; set; }

    public string? Zip { get; set; }

    public string? Location { get; set; }
}
