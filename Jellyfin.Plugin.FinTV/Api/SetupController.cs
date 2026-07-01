using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Setup helper endpoints for Jellyfin Live TV integration.
/// </summary>
[ApiController]
[Route("FinTV/api/setup")]
[AllowAnonymous]
public class SetupController : ControllerBase
{
    /// <summary>
    /// Gets M3U and XMLTV URLs for Jellyfin Live TV configuration.
    /// </summary>
    /// <returns>Setup URLs and instructions.</returns>
    [HttpGet("urls")]
    public ActionResult<object> GetUrls()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request);
        return Ok(new
        {
            baseUrl,
            m3u = $"{baseUrl}/FinTV/iptv/channels.m3u",
            epg = $"{baseUrl}/FinTV/iptv/epg.xml",
            instructions = new[]
            {
                "Dashboard → Live TV → Add Tuner → M3U Tuner",
                "Dashboard → Live TV → Add Guide Provider → XMLTV",
                "Run Refresh Channels, then Refresh Guide"
            }
        });
    }
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
