using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// Ingests ChannelFlow TV client logs and lets the admin UI read them.
/// </summary>
[ApiController]
[Route("api/client-logs")]
public class ClientLogsController : ControllerBase
{
    private readonly ClientLogStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientLogsController"/> class.
    /// </summary>
    /// <param name="store">Client log store.</param>
    public ClientLogsController(ClientLogStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Accepts a batch of log lines from a paired ChannelFlow TV client.
    /// Requires the ChannelFlow API key (<c>X-Api-Key</c> or <c>apiKey</c>).
    /// </summary>
    /// <param name="request">Log batch.</param>
    /// <returns>How many entries were stored.</returns>
    [HttpPost]
    [AllowAnonymous]
    [RequestSizeLimit(524_288)]
    public ActionResult<object> Ingest([FromBody] ClientLogIngestRequest? request)
    {
        var result = _store.Ingest(request);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Message });
        }

        return Ok(new { accepted = result.Accepted, deviceId = result.DeviceId });
    }

    /// <summary>
    /// Lists ChannelFlow TV clients that have sent logs.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "admin")]
    public ActionResult<object> List()
        => Ok(new { devices = _store.ListDevices() });

    /// <summary>
    /// Reads the tail of a client log file.
    /// </summary>
    /// <param name="deviceId">Sanitized device identifier.</param>
    /// <param name="file">Optional log file name.</param>
    /// <param name="tail">Maximum bytes to return from the end of the file.</param>
    [HttpGet("{deviceId}")]
    [Authorize(Policy = "admin")]
    public ActionResult<object> Get(string deviceId, [FromQuery] string? file, [FromQuery] int? tail)
    {
        var detail = _store.GetDevice(deviceId, file, tail);
        if (detail is null)
        {
            return NotFound(new { message = "No logs for that client." });
        }

        return Ok(detail);
    }
}
