using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/logos")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LogosController : ControllerBase
{
    private readonly LogoSetService _logoSets;

    public LogosController(LogoSetService logoSets)
    {
        _logoSets = logoSets;
    }

    [HttpGet("sets")]
    public async Task<ActionResult<object>> GetSets(CancellationToken cancellationToken)
    {
        var sets = await _logoSets.GetAllAsync(cancellationToken);
        return Ok(sets);
    }

    [HttpPost("sets/binarygeek119/sync")]
    public async Task<ActionResult<object>> SyncBinarygeek119(CancellationToken cancellationToken)
    {
        var set = await _logoSets.EnsureBinarygeek119SetAsync(cancellationToken);
        return Ok(set);
    }

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

        return PhysicalFile(path, "image/png");
    }
}
