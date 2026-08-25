using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/music-packs")]
[Authorize(Policy = "admin")]
public class MusicPacksController : ControllerBase
{
    private readonly MusicPackService _packs;

    public MusicPacksController(MusicPackService packs)
    {
        _packs = packs;
    }

    [HttpGet]
    public ActionResult<object> List()
        => Ok(new { packs = _packs.ListPacks() });

    [HttpPost("{id}/download")]
    public ActionResult<object> Download(string id)
    {
        try
        {
            _ = _packs.DownloadAsync(id, CancellationToken.None);
            return Accepted(new { packs = _packs.ListPacks() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public ActionResult<object> Delete(string id)
    {
        try
        {
            _packs.Delete(id);
            return Ok(new { packs = _packs.ListPacks() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
