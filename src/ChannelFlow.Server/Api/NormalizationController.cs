using FinTv.Configuration;
using FinTv.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/normalization")]
[Authorize(Policy = "admin")]
public class NormalizationController : ControllerBase
{
    private readonly StreamNormalizationService _normalization;

    public NormalizationController(StreamNormalizationService normalization)
    {
        _normalization = normalization;
    }

    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
    {
        try
        {
            return Ok(BuildPayload());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load normalization settings: {ex.Message}" });
        }
    }

    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] NormalizationSettingsRequest? request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        try
        {
            plugin.Configuration.Normalization = request.ResetToDefaults == true
                ? NormalizationSettings.CreateDefault()
                : NormalizationTarget.FromSettings(new NormalizationSettings
                {
                    Resolution = request.Resolution ?? NormalizationSettings.DefaultResolution,
                    FrameRate = request.FrameRate ?? NormalizationSettings.DefaultFrameRate,
                    VideoCodec = request.VideoCodec ?? NormalizationSettings.DefaultVideoCodec,
                    VideoProfile = request.VideoProfile ?? NormalizationSettings.DefaultVideoProfile,
                    VideoBitrate = request.VideoBitrate ?? NormalizationSettings.DefaultVideoBitrate,
                    AudioCodec = request.AudioCodec ?? NormalizationSettings.DefaultAudioCodec,
                    AudioChannels = request.AudioChannels ?? NormalizationSettings.DefaultAudioChannels,
                    AudioSampleRate = request.AudioSampleRate ?? NormalizationSettings.DefaultAudioSampleRate,
                    AudioBitrate = request.AudioBitrate ?? NormalizationSettings.DefaultAudioBitrate
                }).ToSettings();
            plugin.SaveConfiguration();
            _normalization.ApplyFromSaved(plugin.Configuration.Normalization);
            return Ok(BuildPayload());
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Could not save normalization settings: {ex.Message}" });
        }
    }

    private object BuildPayload()
        => _normalization.Describe();
}

public class NormalizationSettingsRequest
{
    public string? Resolution { get; set; }

    public string? FrameRate { get; set; }

    public string? VideoCodec { get; set; }

    public string? VideoProfile { get; set; }

    public string? VideoBitrate { get; set; }

    public string? AudioCodec { get; set; }

    public string? AudioChannels { get; set; }

    public string? AudioSampleRate { get; set; }

    public string? AudioBitrate { get; set; }

    public bool? ResetToDefaults { get; set; }
}
