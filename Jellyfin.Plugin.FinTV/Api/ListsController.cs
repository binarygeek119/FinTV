using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// FinTV list registry backed by Jellyfin playlists.
/// </summary>
[ApiController]
[Route("FinTV/api/lists")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ListsController : ControllerBase
{
    private readonly FinTvListService _lists;

    public ListsController(FinTvListService lists)
    {
        _lists = lists;
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _lists.GetAllAsync(cancellationToken);
        return Ok(items.Select(MapListSummary));
    }

    [HttpGet("jellyfin-playlists")]
    public ActionResult<object> GetJellyfinPlaylists([FromQuery] bool unregisteredOnly = false)
    {
        var playlists = unregisteredOnly
            ? _lists.GetUnregisteredJellyfinPlaylists()
            : _lists.GetJellyfinPlaylists();

        return Ok(playlists);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<object>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var list = await _lists.GetByIdAsync(id, cancellationToken);
        if (list is null)
        {
            return NotFound();
        }

        var items = _lists.GetPlaylistItems(list.JellyfinPlaylistId);
        return Ok(new
        {
            list,
            itemCount = items.Count,
            items = items.Take(100).Select(i => new
            {
                id = i.Id,
                name = i.Name,
                type = i.GetType().Name
            })
        });
    }

    [HttpPost]
    public async Task<ActionResult<FinTvList>> Create([FromBody] FinTvListCreateDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _lists.CreateAsync(dto, cancellationToken);
            return Created(string.Empty, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<FinTvList>> Update(Guid id, [FromBody] FinTvListUpdateDto dto, CancellationToken cancellationToken)
    {
        var updated = await _lists.UpdateAsync(id, dto, cancellationToken);
        return updated is null ? NotFound() : updated;
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (await _lists.IsReferencedAsync(id, cancellationToken))
        {
            return BadRequest(new { message = "This list is still referenced by a lineup or special presentation." });
        }

        return await _lists.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }

    private object MapListSummary(FinTvList list)
    {
        return new
        {
            list.Id,
            list.Name,
            list.JellyfinPlaylistId,
            list.PlaybackMode,
            list.CreatedAt,
            itemCount = _lists.GetPlaylistItemCount(list.JellyfinPlaylistId)
        };
    }
}
