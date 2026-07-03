using Jellyfin.Plugin.FinTV;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// REST endpoints for FinTV channel management.
/// </summary>
[ApiController]
[Route("FinTV/api/channels")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ChannelsController : ControllerBase
{
    private readonly ChannelService _channels;
    private readonly StreamService _stream;
    private readonly LineupGeneratorService _lineupGenerator;
    private readonly AiChannelAutoApplyService _aiAutoApply;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelsController"/> class.
    /// </summary>
    /// <param name="channels">Channel service.</param>
    /// <param name="stream">Stream service.</param>
    /// <param name="lineupGenerator">Lineup generator service.</param>
    /// <param name="aiAutoApply">AI auto-apply service.</param>
    public ChannelsController(
        ChannelService channels,
        StreamService stream,
        LineupGeneratorService lineupGenerator,
        AiChannelAutoApplyService aiAutoApply)
    {
        _channels = channels;
        _stream = stream;
        _lineupGenerator = lineupGenerator;
        _aiAutoApply = aiAutoApply;
    }

    /// <summary>
    /// Gets all configured channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of channels.</returns>
    [HttpGet]
    public async Task<ActionResult<List<Channel>>> GetAll(CancellationToken cancellationToken)
    {
        return await _channels.GetAllAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a channel by identifier.
    /// </summary>
    /// <param name="id">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The channel, if found.</returns>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Channel>> Get(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(id, cancellationToken);
        return channel is null ? NotFound() : channel;
    }

    /// <summary>
    /// Gets channels that currently have one or more active IPTV viewers.
    /// </summary>
    /// <returns>Active stream counts keyed by channel.</returns>
    [HttpGet("on-air")]
    public ActionResult<object> GetOnAir()
    {
        return Ok(new { channels = _stream.GetActiveStreams() });
    }

    /// <summary>
    /// Gets the playout item currently airing on a channel, if any.
    /// </summary>
    /// <param name="id">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current playout item or null.</returns>
    [HttpGet("{id:guid}/now-playing")]
    public async Task<ActionResult<object>> GetNowPlaying(Guid id, CancellationToken cancellationToken)
    {
        if (await _channels.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        var current = await _stream.GetCurrentItemAsync(id, cancellationToken);
        return Ok(new { item = current });
    }

    /// <summary>
    /// Creates a new channel with a default 48-slot lineup.
    /// </summary>
    /// <param name="request">Channel definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created channel.</returns>
    [HttpPost]
    public async Task<ActionResult<Channel>> Create([FromBody] ChannelUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _channels.CreateAsync(request.ToChannel(), cancellationToken);
            await BuildWeatherPlayoutIfNeededAsync(created, cancellationToken);
            await _aiAutoApply.TryAutoApplyForChannelAsync(created.Id, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Updates an existing channel.
    /// </summary>
    /// <param name="id">Channel identifier.</param>
    /// <param name="request">Updated channel values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated channel.</returns>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Channel>> Update(Guid id, [FromBody] ChannelUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _channels.UpdateAsync(id, request.ToChannel(), cancellationToken);
            if (updated is null)
            {
                return NotFound();
            }

            await BuildWeatherPlayoutIfNeededAsync(updated, cancellationToken);
            return updated;
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a channel.
    /// </summary>
    /// <param name="id">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content when deleted.</returns>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await _channels.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }

    private async Task BuildWeatherPlayoutIfNeededAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (channel.ContentType != ChannelContentType.Weather)
        {
            return;
        }

        var start = DateTime.UtcNow.Date;
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
        await _lineupGenerator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
    }
}
