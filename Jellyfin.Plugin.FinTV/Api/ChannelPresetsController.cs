using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Ready-made Binarygeek119 channel preset endpoints.
/// </summary>
[ApiController]
[Route("FinTV/api/channels/presets")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ChannelPresetsController : ControllerBase
{
    private readonly ChannelPresetService _presets;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelPresetsController"/> class.
    /// </summary>
    /// <param name="presets">Channel preset service.</param>
    public ChannelPresetsController(ChannelPresetService presets)
    {
        _presets = presets;
    }

    /// <summary>
    /// Gets all ready-made channel presets and whether each already exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preset rows.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ChannelPresetStatus>>> GetAll(
        [FromQuery] ChannelPresetNumberingMode numberingMode,
        CancellationToken cancellationToken)
    {
        return Ok(await _presets.GetStatusAsync(numberingMode, cancellationToken));
    }

    /// <summary>
    /// Creates missing preset channels.
    /// </summary>
    /// <param name="request">Apply options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created, updated, and skipped rows.</returns>
    [HttpPost("apply")]
    [Consumes("application/json")]
    public async Task<ActionResult<ApplyChannelPresetsResult>> Apply(
        [FromBody] ApplyChannelPresetsRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new ApplyChannelPresetsRequest();

        try
        {
            return Ok(await _presets.ApplyAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
