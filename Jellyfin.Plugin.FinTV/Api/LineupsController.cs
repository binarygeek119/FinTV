using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/lineups")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LineupsController : ControllerBase
{
    private readonly LineupService _lineups;
    private readonly LineupGeneratorService _generator;
    private readonly ChannelService _channels;

    public LineupsController(LineupService lineups, LineupGeneratorService generator, ChannelService channels)
    {
        _lineups = lineups;
        _generator = generator;
        _channels = channels;
    }

    [HttpGet("{channelId:guid}")]
    public async Task<ActionResult<object>> GetDefault(Guid channelId, CancellationToken cancellationToken)
    {
        var lineup = await _lineups.GetDefaultLineupAsync(channelId, cancellationToken);
        var overrides = await _lineups.GetOverridesAsync(channelId, cancellationToken);
        return Ok(new { lineup, overrides });
    }

    [HttpPut("{channelId:guid}")]
    public async Task<IActionResult> UpdateDefault(Guid channelId, [FromBody] List<LineupSlotDto> slots, CancellationToken cancellationToken)
    {
        await _lineups.UpdateDefaultSlotsAsync(channelId, slots, cancellationToken);
        return NoContent();
    }

    [HttpPost("{channelId:guid}/overrides")]
    public async Task<ActionResult<LineupOverride>> CreateOverride(Guid channelId, [FromBody] LineupOverrideDto dto, CancellationToken cancellationToken)
    {
        var created = await _lineups.CreateOverrideAsync(channelId, dto, cancellationToken);
        return Created(string.Empty, created);
    }

    [HttpPut("overrides/{overrideId:guid}")]
    public async Task<ActionResult<LineupOverride>> UpdateOverride(Guid overrideId, [FromBody] LineupOverrideDto dto, CancellationToken cancellationToken)
    {
        var updated = await _lineups.UpdateOverrideAsync(overrideId, dto, cancellationToken);
        return updated is null ? NotFound() : updated;
    }

    [HttpDelete("overrides/{overrideId:guid}")]
    public async Task<IActionResult> DeleteOverride(Guid overrideId, CancellationToken cancellationToken)
    {
        return await _lineups.DeleteOverrideAsync(overrideId, cancellationToken) ? NoContent() : NotFound();
    }

    [HttpPost("{channelId:guid}/preview")]
    public async Task<ActionResult<object>> Preview(Guid channelId, [FromBody] PreviewRequest request, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var date = request.Date ?? DateOnly.FromDateTime(DateTime.Now);
        var slots = await _lineups.ResolveSlotsForDateAsync(channelId, date, cancellationToken);
        return Ok(new
        {
            date,
            slots = slots.Select(s => new
            {
                s.SlotIndex,
                candidateCount = s.Candidates.Count,
                candidates = s.Candidates
            })
        });
    }

    [HttpPost("{channelId:guid}/rebuild")]
    public async Task<IActionResult> Rebuild(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(Plugin.Instance?.Configuration.PlayoutDaysToBuild ?? 3);
        await _generator.BuildPlayoutAsync(channel, start, end, cancellationToken);
        return Accepted();
    }
}

public class PreviewRequest
{
    public DateOnly? Date { get; set; }
}
