using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// ChannelFlow list registry backed by Jellyfin playlists.
/// </summary>
[ApiController]
[Route("api/lists")]
[Authorize(Policy = "admin")]
public class ListsController : ControllerBase
{
    private readonly FinTvListService _lists;
    private readonly MusicVideoChannelListService _musicVideos;

    public ListsController(FinTvListService lists, MusicVideoChannelListService musicVideos)
    {
        _lists = lists;
        _musicVideos = musicVideos;
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

    [HttpGet("music-video-channels")]
    public async Task<ActionResult<object>> GetMusicVideoChannels(CancellationToken cancellationToken)
    {
        var channels = await _musicVideos.ListMusicVideoChannelsAsync(cancellationToken);
        return Ok(channels.Select(c => new
        {
            id = c.Id,
            number = c.Number,
            name = c.Name,
            libraryTag = ChannelAiRules.ExtractLibraryTag(c.FilterJson)
        }));
    }

    [HttpGet("music-video-channels/{channelId:guid}/artists")]
    public async Task<ActionResult<object>> GetMusicVideoArtists(Guid channelId, CancellationToken cancellationToken)
    {
        var rows = await _musicVideos.ListArtistsAsync(channelId, cancellationToken);
        return Ok(rows.Select(row => new { id = row.Id, artistName = row.ArtistName }));
    }

    [HttpPost("music-video-channels/{channelId:guid}/artists")]
    public async Task<IActionResult> AddMusicVideoArtist(
        Guid channelId,
        [FromBody] MusicVideoArtistRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = await _musicVideos.AddArtistAsync(channelId, request.ArtistName ?? string.Empty, cancellationToken);
            return Ok(new { id = row.Id, artistName = row.ArtistName });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("music-video-channels/{channelId:guid}/artists/{artistId:guid}")]
    public async Task<IActionResult> DeleteMusicVideoArtist(Guid channelId, Guid artistId, CancellationToken cancellationToken)
        => await _musicVideos.RemoveArtistAsync(channelId, artistId, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("music-video-channels/{channelId:guid}/youtube")]
    public async Task<ActionResult<object>> GetMusicVideoYoutube(Guid channelId, CancellationToken cancellationToken)
    {
        var rows = await _musicVideos.ListYoutubeSourcesAsync(channelId, cancellationToken);
        return Ok(rows.Select(row => new
        {
            id = row.Id,
            sourceUrl = row.SourceUrl,
            title = row.Title,
            artist = row.Artist,
            isPlaylist = row.IsPlaylist,
            youtubeVideoId = row.YoutubeVideoId
        }));
    }

    [HttpPost("music-video-channels/{channelId:guid}/youtube")]
    public async Task<IActionResult> AddMusicVideoYoutube(
        Guid channelId,
        [FromBody] MusicVideoYoutubeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = await _musicVideos.AddYoutubeSourceAsync(channelId, request.Url ?? string.Empty, cancellationToken);
            return Ok(new
            {
                id = row.Id,
                sourceUrl = row.SourceUrl,
                title = row.Title,
                isPlaylist = row.IsPlaylist
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("music-video-channels/{channelId:guid}/youtube/{sourceId:guid}")]
    public async Task<IActionResult> DeleteMusicVideoYoutube(Guid channelId, Guid sourceId, CancellationToken cancellationToken)
        => await _musicVideos.RemoveYoutubeSourceAsync(channelId, sourceId, cancellationToken) ? NoContent() : NotFound();

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

public class MusicVideoArtistRequest
{
    public string? ArtistName { get; set; }
}

public class MusicVideoYoutubeRequest
{
    public string? Url { get; set; }
}
