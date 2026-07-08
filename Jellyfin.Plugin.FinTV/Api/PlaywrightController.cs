using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Playwright Chromium Docker CDP sidecar management.
/// </summary>
[ApiController]
[Route("FinTV/api/playwright")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PlaywrightController : ControllerBase
{
    private readonly PlaywrightDockerBrowserService _docker;

    public PlaywrightController(PlaywrightDockerBrowserService docker)
    {
        _docker = docker;
    }

    [HttpGet("docker/status")]
    public async Task<ActionResult<object>> GetDockerStatus(CancellationToken cancellationToken)
    {
        var status = await _docker.GetStatusAsync(cancellationToken);
        return Ok(ToResponse(status));
    }

    [HttpPost("docker/start")]
    public async Task<ActionResult<object>> StartDocker(CancellationToken cancellationToken)
    {
        try
        {
            await _docker.EnsureBrowserReadyAsync(cancellationToken);
            var status = await _docker.GetStatusAsync(cancellationToken);
            return Ok(ToResponse(status));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (TimeoutException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("docker/stop")]
    public async Task<ActionResult<object>> StopDocker(CancellationToken cancellationToken)
    {
        await _docker.StopAsync(cancellationToken);
        var status = await _docker.GetStatusAsync(cancellationToken);
        return Ok(ToResponse(status));
    }

    private static object ToResponse(PlaywrightDockerStatus status)
    {
        return new
        {
            dockerAvailable = status.DockerAvailable,
            running = status.Running,
            cdpReachable = status.CdpReachable,
            containerName = status.ContainerName,
            image = status.Image,
            cdpPort = status.CdpPort,
            cdpEndpoint = status.CdpEndpoint,
            jellyfinInDocker = status.JellyfinInDocker,
            sharesJellyfinNetwork = status.SharesJellyfinNetwork,
            autoStartOnJellyfinStartup = Plugin.Instance?.Configuration.AutoStartPlaywrightDockerSidecar ?? false
        };
    }
}
