using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Api;

[ApiController]
[Route("api/media-servers")]
[Authorize(Policy = "admin")]
public class MediaServersController : ControllerBase
{
    private readonly MediaServerService _servers;
    private readonly IServiceScopeFactory _scopeFactory;

    public MediaServersController(MediaServerService servers, IServiceScopeFactory scopeFactory)
    {
        _servers = servers;
        _scopeFactory = scopeFactory;
    }

    [HttpGet]
    public async Task<ActionResult<object>> List(CancellationToken cancellationToken)
        => Ok(new
        {
            servers = await _servers.ListAsync(cancellationToken),
            suggestedJellyfinUrl = Environment.GetEnvironmentVariable("JELLYFIN_URL")
        });

    [HttpPost]
    public async Task<ActionResult<object>> Create([FromBody] MediaServerWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _servers.CreateAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<object>> Update(Guid id, [FromBody] MediaServerWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var row = await _servers.UpdateAsync(id, request, cancellationToken);
            return row is null ? NotFound() : Ok(row);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await _servers.DeleteAsync(id, cancellationToken) ? Ok() : NotFound();

    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<object>> Test(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _servers.TestAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/health")]
    public Task<ActionResult<object>> Health(Guid id, CancellationToken cancellationToken)
        => Test(id, cancellationToken);

    [HttpGet("{id:guid}/libraries")]
    public async Task<ActionResult<object>> Browse(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _servers.BrowseLibrariesAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/libraries/refresh")]
    public async Task<ActionResult<object>> Refresh(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _servers.RefreshLibrariesAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Libraries changed while refreshing. Try again." });
        }
    }

    [HttpPut("{id:guid}/libraries")]
    public async Task<ActionResult<object>> SaveLibraries(
        Guid id,
        [FromBody] List<MediaServerLibrarySyncRequest> libraries,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _servers.SaveLibrariesAsync(id, libraries ?? [], cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("sync/progress")]
    public ActionResult<object> SyncProgress() => Ok(_servers.GetSyncProgress());

    [HttpPost("{id:guid}/sync")]
    public async Task<ActionResult<object>> Sync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _servers.EnsureCanSyncAsync(id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new { message = ex.Message, status = _servers.GetSyncProgress() });
            }

            return BadRequest(new { message = ex.Message });
        }

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var servers = scope.ServiceProvider.GetRequiredService<MediaServerService>();
            try
            {
                await servers.SyncAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                scope.ServiceProvider
                    .GetRequiredService<ILogger<MediaServersController>>()
                    .LogError(ex, "Catalog sync failed for {ServerId}", id);
            }
        });

        return Accepted(_servers.GetSyncProgress());
    }

    [HttpGet("{id:guid}/path-mappings")]
    public async Task<ActionResult<object>> GetMappings(Guid id, CancellationToken cancellationToken)
        => Ok(await _servers.GetMappingsAsync(id, cancellationToken));

    [HttpPut("{id:guid}/path-mappings")]
    public async Task<ActionResult<object>> PutMappings(
        Guid id,
        [FromBody] List<PathMapping> mappings,
        CancellationToken cancellationToken)
    {
        await _servers.ReplaceMappingsAsync(id, mappings ?? [], cancellationToken);
        return Ok(await _servers.GetMappingsAsync(id, cancellationToken));
    }

    [HttpPost("{id:guid}/path-mappings/test")]
    public async Task<ActionResult<object>> TestMappings(
        Guid id,
        [FromQuery] int sample = 50,
        CancellationToken cancellationToken = default)
        => Ok(await _servers.TestMappingsAsync(id, sample, cancellationToken));

    [HttpGet("catalog")]
    public Task<object> Catalog(
        [FromQuery] Guid? connectionId,
        [FromQuery] MediaServerKind? kind,
        CancellationToken cancellationToken)
        => _servers.GetCatalogAsync(connectionId, kind, cancellationToken);

    [HttpGet("catalog/episodes")]
    public Task<object> CatalogEpisodes(
        [FromQuery] Guid? connectionId,
        [FromQuery] MediaServerKind? kind,
        [FromQuery] Guid? seriesId,
        [FromQuery] string? seriesName,
        CancellationToken cancellationToken)
        => _servers.GetCatalogEpisodesAsync(connectionId, kind, seriesId, seriesName, cancellationToken);

    [HttpGet("catalog/music")]
    public Task<object> CatalogMusic(
        [FromQuery] Guid? connectionId,
        [FromQuery] MediaServerKind? kind,
        [FromQuery] string? artist,
        CancellationToken cancellationToken)
        => _servers.GetCatalogMusicAsync(connectionId, kind, artist, cancellationToken);

    [HttpGet("catalog/musicvideos")]
    public Task<object> CatalogMusicVideos(
        [FromQuery] Guid? connectionId,
        [FromQuery] MediaServerKind? kind,
        [FromQuery] string? artist,
        CancellationToken cancellationToken)
        => _servers.GetCatalogMusicVideosAsync(connectionId, kind, artist, cancellationToken);

    [HttpGet("removed")]
    public Task<object> Removed(CancellationToken cancellationToken)
        => _servers.GetRemovedAsync(cancellationToken);
}
