using FinTv.Configuration;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/youtube")]
[Authorize(Policy = "admin")]
public class YouTubeController : ControllerBase
{
    private readonly YouTubeCookieStore _cookies;
    private readonly YouTubeCommercialStreamService _youtube;
    private readonly YtDlpLocator _ytDlp;

    public YouTubeController(
        YouTubeCookieStore cookies,
        YouTubeCommercialStreamService youtube,
        YtDlpLocator ytDlp)
    {
        _cookies = cookies;
        _youtube = youtube;
        _ytDlp = ytDlp;
    }

    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
        => Ok(MapSettings());

    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] YouTubeSettingsRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        try
        {
            var settings = plugin.Configuration.YouTube ?? new YouTubeSettings();
            if (request.PreferPremium.HasValue)
            {
                settings.PreferPremium = request.PreferPremium.Value;
            }

            if (request.SponsorBlockEnabled.HasValue)
            {
                settings.SponsorBlockEnabled = request.SponsorBlockEnabled.Value;
            }

            if (request.SponsorBlockCategories is not null)
            {
                settings.SponsorBlockCategories = YouTubeSettings.NormalizeCategories(request.SponsorBlockCategories);
            }

            plugin.Configuration.YouTube = settings;
            plugin.SaveConfiguration();

            if (request.ClearCookies == true)
            {
                _cookies.Clear();
            }
            else if (!string.IsNullOrWhiteSpace(request.Cookies))
            {
                _cookies.Save(request.Cookies);
            }

            return Ok(MapSettings());
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult<object>> Test(CancellationToken cancellationToken)
    {
        var ytDlp = _ytDlp.Resolve();
        if (ytDlp is null)
        {
            return Ok(new
            {
                ok = false,
                message = "yt-dlp was not found. Install yt-dlp on the server or set CHANNELFLOW_YTDLP_PATH."
            });
        }

        var result = await _youtube.TestAccountAsync(cancellationToken);
        return Ok(result);
    }

    private object MapSettings()
    {
        var settings = FinTvRuntime.Current?.Configuration.YouTube ?? new YouTubeSettings();
        var cookies = _cookies.GetStatus();
        return new
        {
            preferPremium = settings.PreferPremium,
            sponsorBlockEnabled = settings.SponsorBlockEnabled,
            sponsorBlockCategories = YouTubeSettings.NormalizeCategories(settings.SponsorBlockCategories),
            knownCategories = YouTubeSettings.KnownCategories,
            hasCookies = cookies.HasCookies,
            cookieCount = cookies.CookieCount,
            looksSignedIn = cookies.LooksSignedIn,
            cookiesSavedAtUtc = cookies.SavedAtUtc,
            ytDlpAvailable = _ytDlp.Resolve() is not null
        };
    }
}

public class YouTubeSettingsRequest
{
    public string? Cookies { get; set; }

    public bool? ClearCookies { get; set; }

    public bool? PreferPremium { get; set; }

    public bool? SponsorBlockEnabled { get; set; }

    public List<string>? SponsorBlockCategories { get; set; }
}
