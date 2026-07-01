using Jellyfin.Plugin.FinTV.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/iptv")]
public class IptvController : ControllerBase
{
    private readonly EpgService _epg;
    private readonly StreamService _stream;

    public IptvController(EpgService epg, StreamService stream)
    {
        _epg = epg;
        _stream = stream;
    }

    [HttpGet("channels.m3u")]
    public async Task<IActionResult> GetM3u(CancellationToken cancellationToken)
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request);
        var content = await _epg.GenerateM3uAsync(baseUrl, cancellationToken);
        return Content(content, "audio/x-mpegurl");
    }

    [HttpGet("epg.xml")]
    public async Task<IActionResult> GetEpg(CancellationToken cancellationToken)
    {
        var content = await _epg.GenerateXmlTvAsync(cancellationToken);
        return Content(content, "application/xml");
    }

    [HttpHead("stream/{channelId}")]
    [HttpGet("stream/{channelId}")]
    public async Task Stream(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out var id))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "video/mp2t";
            return;
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache";
        await _stream.StreamChannelAsync(id, Response.Body, cancellationToken);
    }

    [HttpGet("now/{channelId}")]
    public async Task<IActionResult> GetNow(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out var id))
        {
            return NotFound();
        }

        var current = await _stream.GetCurrentItemAsync(id, cancellationToken);
        return current is null ? NotFound() : Ok(current);
    }
}
