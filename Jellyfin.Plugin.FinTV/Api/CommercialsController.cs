using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/commercials")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CommercialsController : ControllerBase
{
    private readonly CommercialService _commercials;
    private readonly BlackframeChapterTask _blackframeTask;

    public CommercialsController(CommercialService commercials, BlackframeChapterTask blackframeTask)
    {
        _commercials = commercials;
        _blackframeTask = blackframeTask;
    }

    [HttpGet]
    public async Task<ActionResult<List<Commercial>>> GetAll(CancellationToken cancellationToken)
    {
        return await _commercials.GetAllAsync(cancellationToken);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        await _commercials.SyncCommercialLibraryAsync(cancellationToken);
        return Accepted();
    }

    [HttpPost("scan-blackframes")]
    public async Task<IActionResult> ScanBlackframes(CancellationToken cancellationToken)
    {
        await _blackframeTask.ExecuteAsync(new Progress<double>(), cancellationToken);
        return Accepted(new { Plugin.Instance?.Configuration.BlackframeTaskState });
    }

    [HttpGet("scan-status")]
    public ActionResult<object> ScanStatus()
    {
        return Ok(Plugin.Instance?.Configuration.BlackframeTaskState);
    }
}
