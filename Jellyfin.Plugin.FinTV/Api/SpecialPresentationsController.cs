using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Recurring day/time presentation overrides for channels.
/// </summary>
[ApiController]
[Route("FinTV/api/special-presentations")]
[Authorize(Policy = Policies.RequiresElevation)]
public class SpecialPresentationsController : ControllerBase
{
    private readonly SpecialPresentationService _presentations;
    private readonly ChannelService _channels;
    private readonly LineupGeneratorService _generator;

    public SpecialPresentationsController(
        SpecialPresentationService presentations,
        ChannelService channels,
        LineupGeneratorService generator)
    {
        _presentations = presentations;
        _channels = channels;
        _generator = generator;
    }

    [HttpGet("{channelId:guid}")]
    public async Task<ActionResult<object>> GetForChannel(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            return Ok(Array.Empty<object>());
        }

        var items = await _presentations.GetForChannelAsync(channelId, cancellationToken);
        return Ok(items);
    }

    [HttpPost("{channelId:guid}")]
    public async Task<ActionResult<SpecialPresentation>> Create(
        Guid channelId,
        [FromBody] SpecialPresentationDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _presentations.CreateAsync(channelId, dto, cancellationToken);
            await RebuildChannelAsync(channelId, cancellationToken);
            return Created(string.Empty, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SpecialPresentation>> Update(
        Guid id,
        [FromBody] SpecialPresentationDto dto,
        CancellationToken cancellationToken)
    {
        var existing = await _presentations.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        try
        {
            var updated = await _presentations.UpdateAsync(id, dto, cancellationToken);
            await RebuildChannelAsync(existing.ChannelId, cancellationToken);
            return updated!;
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var existing = await _presentations.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        await _presentations.DeleteAsync(id, cancellationToken);
        await RebuildChannelAsync(existing.ChannelId, cancellationToken);
        return NoContent();
    }

    private async Task RebuildChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null || channel.ContentType == ChannelContentType.Weather)
        {
            return;
        }

        var start = DateTime.UtcNow.Date;
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
        await _generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
    }
}
