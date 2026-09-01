using FinTv.Auth;
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
    private readonly PairedTvClientStore _clients;

    public ClientLogsController(ClientLogStore store, PairedTvClientStore clients)
    {
        _store = store;
        _clients = clients;
    }

    /// <summary>
    /// Accepts a batch of log lines from a paired ChannelFlow TV client.
    /// Requires the ChannelFlow API key (<c>X-Api-Key</c> or <c>apiKey</c>).
    /// </summary>
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

        _clients.Touch(ChannelFlowApiAuth.RequestApiKey(HttpContext), new PairedTvClientPresence
        {
            DeviceId = request?.DeviceId,
            DeviceName = request?.DeviceName,
            AppVersion = request?.AppVersion,
            OsVersion = request?.OsVersion
        });

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
