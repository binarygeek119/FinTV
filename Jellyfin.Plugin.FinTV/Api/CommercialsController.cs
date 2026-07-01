using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// REST endpoints for commercial library management and blackframe scanning.
/// </summary>
[ApiController]
[Route("FinTV/api/commercials")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CommercialsController : ControllerBase
{
    private readonly CommercialService _commercials;
    private readonly BlackframeChapterTask _blackframeTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommercialsController"/> class.
    /// </summary>
    /// <param name="commercials">Commercial service.</param>
    /// <param name="blackframeTask">Blackframe detection task.</param>
    public CommercialsController(CommercialService commercials, BlackframeChapterTask blackframeTask)
    {
        _commercials = commercials;
        _blackframeTask = blackframeTask;
    }

    /// <summary>
    /// Gets all commercials in the FinTV library.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of commercials.</returns>
    [HttpGet]
    public async Task<ActionResult<List<Commercial>>> GetAll(CancellationToken cancellationToken)
    {
        return await _commercials.GetAllAsync(cancellationToken);
    }

    /// <summary>
    /// Syncs commercials from the configured Jellyfin library tag.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted when sync starts.</returns>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        await _commercials.SyncCommercialLibraryAsync(cancellationToken);
        return Accepted();
    }

    /// <summary>
    /// Runs FFmpeg blackframe detection on all commercial items.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted with current task state.</returns>
    [HttpPost("scan-blackframes")]
    public async Task<IActionResult> ScanBlackframes(CancellationToken cancellationToken)
    {
        await _blackframeTask.ExecuteAsync(new Progress<double>(), cancellationToken);
        return Accepted(new { Plugin.Instance?.Configuration.BlackframeTaskState });
    }

    /// <summary>
    /// Gets the current blackframe scan task status.
    /// </summary>
    /// <returns>Task progress state.</returns>
    [HttpGet("scan-status")]
    public ActionResult<object> ScanStatus()
    {
        return Ok(Plugin.Instance?.Configuration.BlackframeTaskState);
    }
}
