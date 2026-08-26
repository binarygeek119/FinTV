using FinTv.Auth;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// Delivers encrypted M3U/XMLTV URLs to a waiting app through the pin server.
/// </summary>
[ApiController]
[Route("api/quick-pin")]
[Authorize(Policy = "admin")]
public class QuickPinController : ControllerBase
{
    private readonly QuickPinService _quickPins;
    private readonly IPublicBaseUrl _appHost;

    public QuickPinController(QuickPinService quickPins, IPublicBaseUrl appHost)
    {
        _quickPins = quickPins;
        _appHost = appHost;
    }

    /// <summary>
    /// Encrypts Live TV URLs with the typed PIN and posts ciphertext to the pin server.
    /// </summary>
    [HttpPost("redeem")]
    public async Task<ActionResult> Redeem([FromBody] QuickPinRedeemRequest? request, CancellationToken cancellationToken)
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        var (m3u, xmltv) = PluginApiKey.BuildLiveTvUrls(baseUrl);
        var result = await _quickPins.RedeemAsync(request?.Pin, m3u, xmltv, cancellationToken).ConfigureAwait(false);
        return StatusCode(result.StatusCode, new { ok = result.Ok, message = result.Message });
    }
}

public class QuickPinRedeemRequest
{
    public string? Pin { get; set; }
}
