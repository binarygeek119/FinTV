using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// REST endpoints for channel logo sets and logo file serving.
/// </summary>
[ApiController]
[Route("FinTV/api/logos")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LogosController : ControllerBase
{
    private readonly LogoSetService _logoSets;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogosController"/> class.
    /// </summary>
    /// <param name="logoSets">Logo set service.</param>
    public LogosController(LogoSetService logoSets)
    {
        _logoSets = logoSets;
    }

    /// <summary>
    /// Gets all imported logo sets.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logo sets.</returns>
    [HttpGet("sets")]
    public async Task<ActionResult<object>> GetSets(CancellationToken cancellationToken)
    {
        var sets = await _logoSets.GetAllAsync(cancellationToken);
        return Ok(sets.Select(MapLogoSet));
    }

    /// <summary>
    /// Gets one logo set.
    /// </summary>
    /// <param name="setId">Logo set identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logo set.</returns>
    [HttpGet("sets/{setId:guid}")]
    public async Task<ActionResult<object>> GetSet(Guid setId, CancellationToken cancellationToken)
    {
        var set = await _logoSets.GetByIdAsync(setId, cancellationToken);
        if (set is null)
        {
            return NotFound();
        }

        return Ok(MapLogoSet(set));
    }

    /// <summary>
    /// Creates a custom logo set.
    /// </summary>
    /// <param name="request">Create request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created logo set.</returns>
    [HttpPost("sets/custom")]
    public async Task<ActionResult<object>> CreateCustomSet([FromBody] CreateCustomLogoSetRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var set = await _logoSets.CreateCustomSetAsync(request.Name, cancellationToken);
            return Ok(MapLogoSet(set));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Uploads a logo into a custom logo set.
    /// </summary>
    /// <param name="setId">Logo set identifier.</param>
    /// <param name="file">Image file.</param>
    /// <param name="displayName">Display name shown in the admin UI and channel picker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The uploaded logo entry.</returns>
    [HttpPost("sets/{setId:guid}/logos")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<object>> UploadCustomLogo(
        Guid setId,
        IFormFile file,
        [FromForm] string? displayName,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { message = "Logo file is required." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var entry = await _logoSets.UploadCustomLogoAsync(
                setId,
                stream,
                file.FileName,
                displayName ?? Path.GetFileNameWithoutExtension(file.FileName),
                cancellationToken);
            return Ok(MapLogoEntry(entry));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates a custom logo display name.
    /// </summary>
    /// <param name="setId">Logo set identifier.</param>
    /// <param name="entryId">Logo entry identifier.</param>
    /// <param name="request">Update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated logo entry.</returns>
    [HttpPut("sets/{setId:guid}/entries/{entryId:guid}")]
    public async Task<ActionResult<object>> UpdateCustomLogoEntry(
        Guid setId,
        Guid entryId,
        [FromBody] UpdateLogoEntryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await _logoSets.UpdateCustomLogoAsync(setId, entryId, request.DisplayName, cancellationToken);
            return Ok(MapLogoEntry(entry));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a logo from a custom logo set.
    /// </summary>
    /// <param name="setId">Logo set identifier.</param>
    /// <param name="entryId">Logo entry identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("sets/{setId:guid}/entries/{entryId:guid}")]
    public async Task<IActionResult> DeleteCustomLogoEntry(Guid setId, Guid entryId, CancellationToken cancellationToken)
    {
        try
        {
            await _logoSets.DeleteCustomLogoAsync(setId, entryId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a custom logo set.
    /// </summary>
    /// <param name="setId">Logo set identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("sets/{setId:guid}")]
    public async Task<IActionResult> DeleteCustomSet(Guid setId, CancellationToken cancellationToken)
    {
        try
        {
            await _logoSets.DeleteCustomSetAsync(setId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Imports or refreshes the Binarygeek119 logo set from GitHub.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The synced logo set.</returns>
    [HttpPost("sets/binarygeek119/sync")]
    public async Task<ActionResult<object>> SyncBinarygeek119(CancellationToken cancellationToken)
    {
        var set = await _logoSets.SyncBinarygeek119FromGitHubAsync(cancellationToken);
        return Ok(MapLogoSet(set));
    }

    /// <summary>
    /// Matches channels to preset logos and fills missing logo assignments.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Repair summary.</returns>
    [HttpPost("repair-channels")]
    public async Task<ActionResult<RepairChannelLogosResult>> RepairChannelLogos(CancellationToken cancellationToken)
    {
        var result = await _logoSets.RepairChannelLogosAsync(cancellationToken: cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Serves a channel logo image for M3U and XMLTV clients.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="fileName">Logo file name.</param>
    /// <param name="channels">Channel service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logo image file.</returns>
    [HttpGet("{channelId:guid}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetChannelLogo(Guid channelId, string fileName, [FromServices] ChannelService channels, CancellationToken cancellationToken)
    {
        var channel = await channels.GetByIdAsync(channelId, cancellationToken);
        if (channel?.LogoSetId is null)
        {
            return NotFound();
        }

        var sets = await _logoSets.GetAllAsync(cancellationToken);
        var set = sets.FirstOrDefault(s => s.Id == channel.LogoSetId);
        if (set is null)
        {
            return NotFound();
        }

        var entry = set.Entries.FirstOrDefault(e => e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return NotFound();
        }

        var path = _logoSets.ResolveLogoPath(set, entry.RelativePath);
        if (path is null)
        {
            return NotFound();
        }

        return PhysicalFile(path, GetContentType(path));
    }

    private static object MapLogoSet(LogoSet set)
    {
        return new
        {
            id = set.Id,
            name = set.Name,
            sourceUrl = set.SourceUrl,
            isCustom = LogoSetService.IsCustomSet(set),
            storagePath = set.StoragePath,
            lastSyncedAt = set.LastSyncedAt,
            entries = set.Entries
                .OrderBy(entry => entry.DisplayName ?? entry.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(MapLogoEntry)
                .ToList()
        };
    }

    private static object MapLogoEntry(LogoSetEntry entry)
    {
        return new
        {
            id = entry.Id,
            logoSetId = entry.LogoSetId,
            fileName = entry.FileName,
            relativePath = entry.RelativePath,
            displayName = entry.DisplayName
        };
    }

    private static string GetContentType(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }
}

public class CreateCustomLogoSetRequest
{
    public string Name { get; set; } = string.Empty;
}

public class UpdateLogoEntryRequest
{
    public string DisplayName { get; set; } = string.Empty;
}
