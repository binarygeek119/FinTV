using FinTv.Auth;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// Lists and revokes paired ChannelFlow TV clients, and lets a TV claim or drop its key.
/// </summary>
[ApiController]
[Route("api/clients")]
public class ClientsController : ControllerBase
{
    private readonly PairedTvClientStore _clients;

    public ClientsController(PairedTvClientStore clients)
    {
        _clients = clients;
    }

    /// <summary>
    /// Lists paired ChannelFlow TV apps for the Clients tab.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "admin")]
    public ActionResult<object> List()
        => Ok(new { clients = _clients.List() });

    /// <summary>
    /// Revokes a client from the admin UI. The TV's next request is 401 and it drops this server.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "admin")]
    public ActionResult Remove(Guid id)
    {
        if (!_clients.Remove(id))
        {
            return NotFound(new { message = "That client is not paired." });
        }

        return Ok(new { ok = true });
    }

    /// <summary>
    /// ChannelFlow TV heartbeat. Mints a unique key when the TV still has the shared plugin key.
    /// </summary>
    [HttpPost("session")]
    [AllowAnonymous]
    public ActionResult<PairedTvClientSessionResult> Session([FromBody] PairedTvClientSessionRequest? request)
    {
        var apiKey = ChannelFlowApiAuth.RequestApiKey(HttpContext);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unauthorized(new { message = "Invalid API key.", code = ChannelFlowApiAuth.RevokedCode });
        }

        try
        {
            return Ok(_clients.OpenSession(apiKey, request));
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { message = "Invalid API key.", code = ChannelFlowApiAuth.RevokedCode });
        }
    }

    /// <summary>
    /// ChannelFlow TV destroyed its key locally and is telling the server to forget it.
    /// </summary>
    [HttpDelete("me")]
    [AllowAnonymous]
    public ActionResult ForgetMe()
    {
        var apiKey = ChannelFlowApiAuth.RequestApiKey(HttpContext);
        if (string.IsNullOrWhiteSpace(apiKey) || PluginApiKey.Matches(apiKey))
        {
            return Ok(new { ok = true });
        }

        _clients.RemoveByApiKey(apiKey);
        return Ok(new { ok = true });
    }
}
