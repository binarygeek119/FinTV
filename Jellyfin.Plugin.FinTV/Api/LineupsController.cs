using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly FinTvDbContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineupsController"/> class.
    /// </summary>
    /// <param name="lineups">Lineup service.</param>
    /// <param name="generator">Playout generator service.</param>
    /// <param name="channels">Channel service.</param>
    /// <param name="db">Database context.</param>
    public LineupsController(
        LineupService lineups,
        LineupGeneratorService generator,
        ChannelService channels,
        FinTvDbContext db)
    {
        _lineups = lineups;
        _generator = generator;
        _channels = channels;
        _db = db;
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
        if (channel.ContentType == ChannelContentType.Weather)
        {
            return Ok(new
            {
                lineup = new
                {
                    slots = new[]
                    {
                        new
                        {
                            slotIndex = 0,
                            spanSlots = 48,
                            candidates = Array.Empty<object>()
                        }
                    }
                },
                overrides = Array.Empty<object>(),
                contentType = channel.ContentType,
                isWeather = true
            });
        }

        return Ok(new
        {
            lineup,
            overrides,
            contentType = channel.ContentType,
            isWeather = false
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
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            return BadRequest(new { message = "Weather channels do not use editable lineups." });
        }

        await _lineups.UpdateDefaultSlotsAsync(channelId, slots, cancellationToken);

        if (channel.ContentType != ChannelContentType.Weather)
        {
            await RebuildPlayoutAsync(channel, cancellationToken);
        }

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
        try
        {
            var created = await _lineups.CreateOverrideAsync(channelId, dto, cancellationToken);
            return Created(string.Empty, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
                description = "Live WeatherStar feed streaming 24/7 as one continuous programme.",
                slots = new[]
                {
                    new
                    {
                        slotIndex = 0,
                        spanSlots = 48,
                        candidateCount = 1,
                        candidates = new[]
                        {
                            new
                            {
                                title = "Local Weather (live 24/7)",
                                kind = "weather"
                            }
                        }
                    }
                }
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

        await RebuildPlayoutAsync(channel, cancellationToken);
        return Accepted();
    }

    private async Task RebuildPlayoutAsync(Channel channel, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow.Date;
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
        await _generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    [HttpGet("{channelId:guid}/playout-horizon")]
    public async Task<ActionResult<object>> GetPlayoutHorizon(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);
        var latestFinish = await _db.PlayoutItems
            .Where(p => p.ChannelId == channelId && p.Finish > now)
            .Select(p => (DateTime?)p.Finish)
            .MaxAsync(cancellationToken);

        return Ok(new
        {
            playoutDaysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild(),
            horizonEndUtc = horizonEnd,
            latestScheduledFinishUtc = latestFinish,
            daysBuilt = latestFinish.HasValue
                ? Math.Max(0, (latestFinish.Value - now).TotalDays)
                : 0
        });
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
