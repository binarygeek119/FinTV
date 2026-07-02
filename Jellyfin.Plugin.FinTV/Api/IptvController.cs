using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// IPTV endpoints for M3U, XMLTV, and live MPEG-TS streams.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("FinTV/iptv")]
public class IptvController : ControllerBase
{
    private readonly EpgService _epg;
    private readonly StreamService _stream;
    private readonly IServerApplicationHost _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="IptvController"/> class.
    /// </summary>
    /// <param name="epg">EPG service.</param>
    /// <param name="stream">Stream service.</param>
    /// <param name="appHost">Server application host.</param>
    public IptvController(EpgService epg, StreamService stream, IServerApplicationHost appHost)
    {
        _epg = epg;
        _stream = stream;
        _appHost = appHost;
    }

    /// <summary>
    /// Gets the M3U playlist for all enabled channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>M3U playlist content.</returns>
    [HttpGet("channels.m3u")]
    public async Task<IActionResult> GetM3u(CancellationToken cancellationToken)
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var content = await _epg.GenerateM3uAsync(baseUrl, cancellationToken);
        return Content(content, "audio/x-mpegurl");
    }

    /// <summary>
    /// Gets the XMLTV electronic program guide.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XMLTV guide content.</returns>
    [HttpGet("epg.xml")]
    public async Task<IActionResult> GetEpg(CancellationToken cancellationToken)
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var content = await _epg.GenerateXmlTvBytesAsync(baseUrl, cancellationToken);
        return File(content, "application/xml; charset=utf-8");
    }

    /// <summary>
    /// Streams the current MPEG-TS output for a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Live transport stream.</returns>
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

    /// <summary>
    /// Gets the playout item currently airing on a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current playout item.</returns>
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
