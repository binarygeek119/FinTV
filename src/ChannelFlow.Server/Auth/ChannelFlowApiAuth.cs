using Microsoft.AspNetCore.Http;

namespace FinTv.Auth;

/// <summary>
/// HTTP context keys for the IPTV / client API key that passed <see cref="ApiKeyMiddleware"/>.
/// </summary>
public static class ChannelFlowApiAuth
{
    public const string ApiKeyItem = "ChannelFlow.ApiKey";
    public const string ClientItem = "ChannelFlow.PairedClient";
    public const string RevokedCode = "client_revoked";

    public static string? RequestApiKey(HttpContext http)
        => http.Items[ApiKeyItem] as string;

    public static PairedTvClient? RequestClient(HttpContext http)
        => http.Items[ClientItem] as PairedTvClient;
}
