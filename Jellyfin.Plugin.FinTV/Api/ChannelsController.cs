using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/channels")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ChannelsController : ControllerBase
{
    private readonly ChannelService _channels;

    public ChannelsController(ChannelService channels)
    {
        _channels = channels;
    }

    [HttpGet]
    public async Task<ActionResult<List<Channel>>> GetAll(CancellationToken cancellationToken)
    {
        return await _channels.GetAllAsync(cancellationToken);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Channel>> Get(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _channels.GetByIdAsync(id, cancellationToken);
        return channel is null ? NotFound() : channel;
    }

    [HttpPost]
    public async Task<ActionResult<Channel>> Create([FromBody] Channel channel, CancellationToken cancellationToken)
    {
        var created = await _channels.CreateAsync(channel, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Channel>> Update(Guid id, [FromBody] Channel channel, CancellationToken cancellationToken)
    {
        var updated = await _channels.UpdateAsync(id, channel, cancellationToken);
        return updated is null ? NotFound() : updated;
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await _channels.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }
}
