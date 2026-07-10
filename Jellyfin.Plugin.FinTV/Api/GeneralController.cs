using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Services;
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
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var scheduleTimeZone = ScheduleTimeZoneHelper.NormalizeTimeZoneId(config.ScheduleTimeZone);
            return Ok(new
            {
                debugLogging = config.DebugLogging,
                scheduleTimeZone,
                playoutDaysToBuild = config.PlayoutDaysToBuild
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load general settings: {ex.Message}" });
        }
    }

    /// <summary>
    /// Lists time zones available on this server for the schedule dropdown.
    /// </summary>
    [HttpGet("timezones")]
    public ActionResult<object> GetTimeZones()
    {
        try
        {
            var timeZones = ScheduleTimeZoneHelper.GetAvailableTimeZones()
                .Select(tz => new
                {
                    id = tz.Id,
                    label = tz.Label,
                    offset = tz.Offset
                });

            return Ok(timeZones);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load time zones: {ex.Message}" });
        }
    }

    /// <summary>
    /// Updates general plugin settings.
    /// </summary>
    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] GeneralSettingsRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        try
        {
            if (request.DebugLogging.HasValue)
            {
                plugin.Configuration.DebugLogging = request.DebugLogging.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.ScheduleTimeZone))
            {
                var normalized = ScheduleTimeZoneHelper.NormalizeTimeZoneId(request.ScheduleTimeZone);
                if (!ScheduleTimeZoneHelper.TryResolveScheduleTimeZone(normalized, out _, out var resolvedId))
                {
                    return BadRequest(new { message = $"Time zone '{request.ScheduleTimeZone}' is not available on this server." });
                }

                plugin.Configuration.ScheduleTimeZone = resolvedId;
            }

            if (request.PlayoutDaysToBuild.HasValue)
            {
                plugin.Configuration.PlayoutDaysToBuild = Math.Clamp(request.PlayoutDaysToBuild.Value, 1, 14);
            }

            plugin.SaveConfiguration();
            return Ok(new
            {
                saved = true,
                debugLogging = plugin.Configuration.DebugLogging,
                scheduleTimeZone = plugin.Configuration.ScheduleTimeZone
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Could not save general settings: {ex.Message}" });
        }
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
