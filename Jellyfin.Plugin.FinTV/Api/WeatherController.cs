using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// WeatherStar settings and self-hosted ws4kp/ws3kp Docker management.
/// </summary>
[ApiController]
[Route("FinTV/api/weather")]
[Authorize(Policy = Policies.RequiresElevation)]
public class WeatherController : ControllerBase
{
    private readonly WeatherStarDockerService _docker;

    public WeatherController(WeatherStarDockerService docker)
    {
        _docker = docker;
    }

    [HttpGet("docker/status")]
    public async Task<ActionResult<object>> GetDockerStatus(CancellationToken cancellationToken)
    {
        var status = await _docker.GetCombinedStatusAsync(cancellationToken);
        return Ok(ToCombinedResponse(status));
    }

    [HttpPost("docker/ws4kp/start")]
    public Task<ActionResult<object>> StartWs4kp(
        [FromBody] WeatherStarDockerStartRequest? request,
        CancellationToken cancellationToken)
        => StartVariantAsync(WeatherStarDockerVariant.Ws4kp, request, cancellationToken);

    [HttpPost("docker/ws4kp/stop")]
    public async Task<ActionResult<object>> StopWs4kp(CancellationToken cancellationToken)
    {
        await _docker.StopAsync(WeatherStarDockerVariant.Ws4kp, cancellationToken);
        var status = await _docker.GetCombinedStatusAsync(cancellationToken);
        return Ok(ToCombinedResponse(status));
    }

    [HttpPost("docker/ws3kp/start")]
    public Task<ActionResult<object>> StartWs3kp(
        [FromBody] WeatherStarDockerStartRequest? request,
        CancellationToken cancellationToken)
        => StartVariantAsync(WeatherStarDockerVariant.Ws3kp, request, cancellationToken);

    [HttpPost("docker/ws3kp/stop")]
    public async Task<ActionResult<object>> StopWs3kp(CancellationToken cancellationToken)
    {
        await _docker.StopAsync(WeatherStarDockerVariant.Ws3kp, cancellationToken);
        var status = await _docker.GetCombinedStatusAsync(cancellationToken);
        return Ok(ToCombinedResponse(status));
    }

    private async Task<ActionResult<object>> StartVariantAsync(
        WeatherStarDockerVariant variant,
        WeatherStarDockerStartRequest? request,
        CancellationToken cancellationToken)
    {
        _docker.UpdateSettings(variant, request?.HostPort, request?.Image);
        var status = await _docker.EnsureRunningAsync(variant, cancellationToken);

        if (request?.UpdateBaseUrl != false)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
            plugin.Configuration.WeatherStarBaseUrl = status.BaseUrl;
            plugin.SaveConfiguration();
        }
        else if (Plugin.Instance is not null)
        {
            Plugin.Instance.SaveConfiguration();
        }

        var combined = await _docker.GetCombinedStatusAsync(cancellationToken);
        return Ok(ToCombinedResponse(combined));
    }

    private static object ToCombinedResponse(WeatherStarDockerCombinedStatus status)
    {
        return new
        {
            ws4kp = ToVariantResponse(status.Ws4kp),
            ws3kp = ToVariantResponse(status.Ws3kp),
            configuredBaseUrl = status.ConfiguredBaseUrl,
            usingLocalWs4kp = status.UsingLocalWs4kp,
            usingLocalWs3kp = status.UsingLocalWs3kp
        };
    }

    private static object ToVariantResponse(WeatherStarDockerStatus status)
    {
        return new
        {
            dockerAvailable = status.DockerAvailable,
            running = status.Running,
            containerName = status.ContainerName,
            image = status.Image,
            hostPort = status.HostPort,
            baseUrl = status.BaseUrl
        };
    }
}

public class WeatherStarDockerStartRequest
{
    public int? HostPort { get; set; }

    public string? Image { get; set; }

    public bool UpdateBaseUrl { get; set; } = true;
}
