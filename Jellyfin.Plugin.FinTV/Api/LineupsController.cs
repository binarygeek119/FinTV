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
    private readonly ChannelService _channels;
    private readonly FinTvDbContext _db;
    private readonly PlayoutBuilderService _playoutBuilder;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineupsController"/> class.
    /// </summary>
    /// <param name="lineups">Lineup service.</param>
    /// <param name="channels">Channel service.</param>
    /// <param name="db">Database context.</param>
    /// <param name="playoutBuilder">Background playout builder.</param>
    public LineupsController(
        LineupService lineups,
        ChannelService channels,
        FinTvDbContext db,
        PlayoutBuilderService playoutBuilder)
    {
        _lineups = lineups;
        _channels = channels;
        _db = db;
        _playoutBuilder = playoutBuilder;
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
            var slots = WeatherLineupHelper.CreateDailySlots();
            return Ok(new
            {
                lineup = new
                {
                    slots = slots.Select(s => new
                    {
                        s.SlotIndex,
                        s.SpanSlots,
                        candidates = Array.Empty<object>()
                    })
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
            _playoutBuilder.QueueRebuildChannel(channel.Id);
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
            var weatherSlots = await _lineups.ResolveSlotsForDateAsync(channelId, date, cancellationToken);
            return Ok(new
            {
                date,
                isWeather = true,
                title = "Local Weather",
                description = "Live WeatherStar feed with 24 one-hour programme blocks per day.",
                slots = weatherSlots.Select(s => new
                {
                    s.SlotIndex,
                    spanSlots = s.SpanSlots,
                    candidateCount = 1,
                    candidates = new[]
                    {
                        new
                        {
                            title = "Local Weather (live)",
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

        _playoutBuilder.QueueRebuildChannel(channel.Id);
        return Accepted(new { queued = true, channelId, channelName = channel.Name });
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
