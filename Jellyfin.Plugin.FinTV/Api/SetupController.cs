using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Setup helper endpoints for Jellyfin Live TV integration.
/// </summary>
[ApiController]
[Route("FinTV/api/setup")]
public class SetupController : ControllerBase
{
    private readonly IServerApplicationHost _appHost;
    private readonly JellyfinCatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetupController"/> class.
    /// </summary>
    /// <param name="appHost">Server application host.</param>
    /// <param name="catalog">Jellyfin catalog service.</param>
    public SetupController(IServerApplicationHost appHost, JellyfinCatalogService catalog)
    {
        _appHost = appHost;
        _catalog = catalog;
    }

    /// <summary>
    /// Gets M3U and XMLTV URLs for Jellyfin Live TV configuration.
    /// </summary>
    /// <returns>Setup URLs and instructions.</returns>
    [HttpGet("urls")]
    [AllowAnonymous]
    public ActionResult<object> GetUrls()
    {
        return Ok(BuildUrlResponse());
    }

    /// <summary>
    /// Gets FinTV setup settings for the admin UI.
    /// </summary>
    /// <returns>Setup settings.</returns>
    [HttpGet("settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetSettings()
    {
        return Ok(new
        {
            publicBaseUrl = Plugin.Instance?.Configuration.PublicBaseUrl ?? string.Empty,
            ebsBackgroundMusicSource = (int)(Plugin.Instance?.Configuration.EbsBackgroundMusicSource ?? EbsBackgroundMusicSource.NamedLibrary),
            ebsBackgroundMusicLibraryName = Plugin.Instance?.Configuration.EbsBackgroundMusicLibraryName ?? "Background Music",
            ebsBackgroundMusicLibraryId = Plugin.Instance?.Configuration.EbsBackgroundMusicLibraryId ?? string.Empty,
            weatherStarBaseUrl = Plugin.Instance?.Configuration.WeatherStarBaseUrl ?? WeatherStarChannelService.DefaultWeatherStarBaseUrl,
            weatherStarPermalinkQuery = Plugin.Instance?.Configuration.WeatherStarPermalinkQuery
                ?? WeatherStarChannelService.DefaultWeatherStarPermalinkQuery,
            autoStartPlaywrightDockerSidecar = Plugin.Instance?.Configuration.AutoStartPlaywrightDockerSidecar ?? false,
            autoStartWeatherStarDocker = Plugin.Instance?.Configuration.AutoStartWeatherStarDocker ?? false,
            weatherStarAutoWideForSixteenNine = Plugin.Instance?.Configuration.WeatherStarAutoWideForSixteenNine ?? true,
            playoutDaysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild(),
            ws4kpHostPort = Plugin.Instance?.Configuration.Ws4kp.HostPort ?? 8080,
            ws4kpImage = Plugin.Instance?.Configuration.Ws4kp.Image ?? "ghcr.io/netbymatt/ws4kp",
            ws3kpHostPort = Plugin.Instance?.Configuration.Ws3kp.HostPort ?? 8083,
            ws3kpImage = Plugin.Instance?.Configuration.Ws3kp.Image ?? "ghcr.io/netbymatt/ws3kp",
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name })
        });
    }

    /// <summary>
    /// Updates FinTV setup settings and returns refreshed Live TV URLs.
    /// </summary>
    /// <param name="request">Setup settings.</param>
    /// <returns>Updated setup URLs.</returns>
    [HttpPut("settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> UpdateSettings([FromBody] SetupSettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.PublicBaseUrl = string.IsNullOrWhiteSpace(request.PublicBaseUrl)
            ? null
            : request.PublicBaseUrl.Trim().TrimEnd('/');

        if (request.EbsBackgroundMusicSource.HasValue)
        {
            plugin.Configuration.EbsBackgroundMusicSource = request.EbsBackgroundMusicSource.Value;
        }

        if (request.EbsBackgroundMusicLibraryName is not null)
        {
            plugin.Configuration.EbsBackgroundMusicLibraryName = request.EbsBackgroundMusicLibraryName.Trim();
        }

        plugin.Configuration.EbsBackgroundMusicLibraryId = string.IsNullOrWhiteSpace(request.EbsBackgroundMusicLibraryId)
            ? null
            : request.EbsBackgroundMusicLibraryId.Trim();

        if (request.WeatherStarBaseUrl is not null)
        {
            plugin.Configuration.WeatherStarBaseUrl = WeatherStarChannelService.NormalizeWeatherStarBaseUrl(request.WeatherStarBaseUrl);
        }

        if (request.WeatherStarPermalinkQuery is not null)
        {
            plugin.Configuration.WeatherStarPermalinkQuery =
                WeatherStarChannelService.NormalizePermalinkQuery(request.WeatherStarPermalinkQuery);
        }

        if (request.WeatherStarFullPermalink is not null
            && !string.IsNullOrWhiteSpace(request.WeatherStarFullPermalink))
        {
            var split = WeatherStarChannelService.SplitPermalink(request.WeatherStarFullPermalink);
            plugin.Configuration.WeatherStarBaseUrl = split.BaseUrl;
            plugin.Configuration.WeatherStarPermalinkQuery = split.Query;
        }

        if (request.AutoStartPlaywrightDockerSidecar.HasValue)
        {
            plugin.Configuration.AutoStartPlaywrightDockerSidecar = request.AutoStartPlaywrightDockerSidecar.Value;
        }

        if (request.AutoStartWeatherStarDocker.HasValue)
        {
            plugin.Configuration.AutoStartWeatherStarDocker = request.AutoStartWeatherStarDocker.Value;
        }

        if (request.WeatherStarAutoWideForSixteenNine.HasValue)
        {
            plugin.Configuration.WeatherStarAutoWideForSixteenNine = request.WeatherStarAutoWideForSixteenNine.Value;
        }

        plugin.SaveConfiguration();

        return Ok(BuildUrlResponse());
    }

    private object BuildUrlResponse()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        return new
        {
            baseUrl,
            m3u = $"{baseUrl}/FinTV/iptv/channels.m3u",
            epg = $"{baseUrl}/FinTV/iptv/epg.xml",
            instructions = new[]
            {
                "Dashboard → Live TV → Add Tuner → M3U Tuner",
                "Paste the M3U Tuner URL above (must be reachable by the Jellyfin server)",
                "Dashboard → Live TV → Add Guide Provider → XMLTV",
                "Paste the XMLTV Guide URL above, then Refresh Channels and Refresh Guide"
            }
        };
    }
}

/// <summary>
/// Setup settings payload.
/// </summary>
public class SetupSettingsRequest
{
    /// <summary>
    /// Gets or sets the public base URL used in generated M3U/XMLTV links.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets where EBS off-air background music is selected from.
    /// </summary>
    public EbsBackgroundMusicSource? EbsBackgroundMusicSource { get; set; }

    /// <summary>
    /// Gets or sets the selected music library name for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the selected music library identifier for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryId { get; set; }

    /// <summary>
    /// Gets or sets the WeatherStar 4000 base URL used by weather channels.
    /// </summary>
    public string? WeatherStarBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the ws4kp permalink query string (display settings without location).
    /// </summary>
    public string? WeatherStarPermalinkQuery { get; set; }

    /// <summary>
    /// Gets or sets a full WeatherStar permalink; FinTV splits it into base URL and display settings.
    /// </summary>
    public string? WeatherStarFullPermalink { get; set; }

    /// <summary>
    /// Gets or sets whether the Playwright Docker CDP sidecar starts during Jellyfin startup.
    /// </summary>
    public bool? AutoStartPlaywrightDockerSidecar { get; set; }

    /// <summary>
    /// Gets or sets whether the self-hosted WeatherStar Docker container starts during Jellyfin startup.
    /// </summary>
    public bool? AutoStartWeatherStarDocker { get; set; }

    /// <summary>
    /// Gets or sets whether weather capture auto-sets wide=true for 16:9 channels (and wide=false for 4:3).
    /// </summary>
    public bool? WeatherStarAutoWideForSixteenNine { get; set; }
}

/// <summary>
/// Background task endpoints for FinTV maintenance.
/// </summary>
[ApiController]
[Route("FinTV/api/tasks")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TasksController : ControllerBase
{
    private readonly PlayoutBuilderService _playoutBuilder;

    /// <summary>
    /// Initializes a new instance of the <see cref="TasksController"/> class.
    /// </summary>
    /// <param name="playoutBuilder">Playout builder service.</param>
    public TasksController(PlayoutBuilderService playoutBuilder)
    {
        _playoutBuilder = playoutBuilder;
    }

    /// <summary>
    /// Rebuilds playout timelines for all enabled channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted when rebuild starts.</returns>
    [HttpPost("rebuild-all")]
    public async Task<IActionResult> RebuildAll(CancellationToken cancellationToken)
    {
        await _playoutBuilder.BuildAllChannelsAsync(cancellationToken);
        return Accepted();
    }
}
