using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// REST endpoints for lineup editing, previews, and playout rebuilds.
/// </summary>
[ApiController]
[Route("FinTV/api/lineups")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LineupsController : ControllerBase
{
    private readonly LineupService _lineups;
    private readonly LineupGeneratorService _generator;
    private readonly ChannelService _channels;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineupsController"/> class.
    /// </summary>
    /// <param name="lineups">Lineup service.</param>
    /// <param name="generator">Playout generator service.</param>
    /// <param name="channels">Channel service.</param>
    public LineupsController(LineupService lineups, LineupGeneratorService generator, ChannelService channels)
    {
        _lineups = lineups;
        _generator = generator;
        _channels = channels;
    }

    /// <summary>
    /// Gets the default lineup and overrides for a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Default lineup and override definitions.</returns>
    [HttpGet("{channelId:guid}")]
    public async Task<ActionResult<object>> GetDefault(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var lineup = await _lineups.GetDefaultLineupAsync(channelId, cancellationToken);
        var overrides = await _lineups.GetOverridesAsync(channelId, cancellationToken);
        return Ok(new
        {
            lineup,
            overrides,
            contentType = channel.ContentType,
            isWeather = channel.ContentType == ChannelContentType.Weather
        });
    }

    /// <summary>
    /// Updates the default 48-slot lineup for a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="slots">Slot definitions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content when saved.</returns>
    [HttpPut("{channelId:guid}")]
    public async Task<IActionResult> UpdateDefault(Guid channelId, [FromBody] List<LineupSlotDto> slots, CancellationToken cancellationToken)
    {
        await _lineups.UpdateDefaultSlotsAsync(channelId, slots, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Creates a special-day lineup override.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="dto">Override definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created override.</returns>
    [HttpPost("{channelId:guid}/overrides")]
    public async Task<ActionResult<LineupOverride>> CreateOverride(Guid channelId, [FromBody] LineupOverrideDto dto, CancellationToken cancellationToken)
    {
        var created = await _lineups.CreateOverrideAsync(channelId, dto, cancellationToken);
        return Created(string.Empty, created);
    }

    /// <summary>
    /// Updates an existing lineup override.
    /// </summary>
    /// <param name="overrideId">Override identifier.</param>
    /// <param name="dto">Updated override definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated override.</returns>
    [HttpPut("overrides/{overrideId:guid}")]
    public async Task<ActionResult<LineupOverride>> UpdateOverride(Guid overrideId, [FromBody] LineupOverrideDto dto, CancellationToken cancellationToken)
    {
        var updated = await _lineups.UpdateOverrideAsync(overrideId, dto, cancellationToken);
        return updated is null ? NotFound() : updated;
    }

    /// <summary>
    /// Deletes a lineup override.
    /// </summary>
    /// <param name="overrideId">Override identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content when deleted.</returns>
    [HttpDelete("overrides/{overrideId:guid}")]
    public async Task<IActionResult> DeleteOverride(Guid overrideId, CancellationToken cancellationToken)
    {
        return await _lineups.DeleteOverrideAsync(overrideId, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Previews resolved lineup slots for a given date.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="request">Preview request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved slots for the date.</returns>
    [HttpPost("{channelId:guid}/preview")]
    public async Task<ActionResult<object>> Preview(Guid channelId, [FromBody] PreviewRequest request, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var date = request.Date ?? DateOnly.FromDateTime(DateTime.Now);
        if (channel.ContentType == ChannelContentType.Weather)
        {
            return Ok(new
            {
                date,
                isWeather = true,
                title = "Local Weather",
                description = "Live WeatherStar feed streaming 24/7. Lineup slots are not used for weather channels.",
                slots = Enumerable.Range(0, 48).Select(slotIndex => new
                {
                    slotIndex,
                    candidateCount = 1,
                    candidates = new[]
                    {
                        new
                        {
                            title = "Local Weather (live 24/7)",
                            kind = "weather"
                        }
                    }
                })
            });
        }

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

    /// <summary>
    /// Rebuilds the playout timeline for a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted when rebuild starts.</returns>
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

/// <summary>
/// Request body for lineup preview.
/// </summary>
public class PreviewRequest
{
    /// <summary>
    /// Gets or sets the date to preview. Defaults to today.
    /// </summary>
    public DateOnly? Date { get; set; }
}
