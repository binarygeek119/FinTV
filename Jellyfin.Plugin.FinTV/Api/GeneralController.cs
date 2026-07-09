using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// General FinTV plugin settings for the admin UI.
/// </summary>
[ApiController]
[Route("FinTV/api/general")]
[Authorize(Policy = Policies.RequiresElevation)]
public class GeneralController : ControllerBase
{
    /// <summary>
    /// Gets general plugin settings.
    /// </summary>
    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(new
        {
            debugLogging = config.DebugLogging,
            scheduleTimeZone = config.ScheduleTimeZone,
            playoutDaysToBuild = config.PlayoutDaysToBuild
        });
    }

    /// <summary>
    /// Updates general plugin settings.
    /// </summary>
    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] GeneralSettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request.DebugLogging.HasValue)
        {
            plugin.Configuration.DebugLogging = request.DebugLogging.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.ScheduleTimeZone))
        {
            plugin.Configuration.ScheduleTimeZone = request.ScheduleTimeZone.Trim();
        }

        if (request.PlayoutDaysToBuild.HasValue)
        {
            plugin.Configuration.PlayoutDaysToBuild = Math.Clamp(request.PlayoutDaysToBuild.Value, 1, 14);
        }

        plugin.SaveConfiguration();
        return Ok(new { saved = true, debugLogging = plugin.Configuration.DebugLogging });
    }
}

/// <summary>
/// General settings payload.
/// </summary>
public class GeneralSettingsRequest
{
    /// <summary>
    /// Gets or sets whether verbose FinTV debug logging is enabled.
    /// </summary>
    public bool? DebugLogging { get; set; }

    /// <summary>
    /// Gets or sets the schedule time zone IANA id.
    /// </summary>
    public string? ScheduleTimeZone { get; set; }

    /// <summary>
    /// Gets or sets how many days of playout to build (1-14).
    /// </summary>
    public int? PlayoutDaysToBuild { get; set; }
}
