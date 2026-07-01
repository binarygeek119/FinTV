using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/setup")]
[AllowAnonymous]
public class SetupController : ControllerBase
{
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

[ApiController]
[Route("FinTV/api/tasks")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TasksController : ControllerBase
{
    private readonly PlayoutBuilderService _playoutBuilder;

    public TasksController(PlayoutBuilderService playoutBuilder)
    {
        _playoutBuilder = playoutBuilder;
    }

    [HttpPost("rebuild-all")]
    public async Task<IActionResult> RebuildAll(CancellationToken cancellationToken)
    {
        await _playoutBuilder.BuildAllChannelsAsync(cancellationToken);
        return Accepted();
    }
}
