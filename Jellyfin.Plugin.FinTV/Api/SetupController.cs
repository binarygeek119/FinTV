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

    /// <summary>
    /// Initializes a new instance of the <see cref="SetupController"/> class.
    /// </summary>
    /// <param name="appHost">Server application host.</param>
    public SetupController(IServerApplicationHost appHost)
    {
        _appHost = appHost;
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
            publicBaseUrl = Plugin.Instance?.Configuration.PublicBaseUrl ?? string.Empty
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
