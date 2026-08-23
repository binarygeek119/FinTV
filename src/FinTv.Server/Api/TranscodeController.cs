using System.Text;
using CliWrap;
using FinTv;
using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/transcode")]
[Authorize(Policy = "admin")]
public class TranscodeController : ControllerBase
{
    private readonly FfmpegEncodingService _encoding;
    private readonly IFfmpegLocator _ffmpeg;

    public TranscodeController(FfmpegEncodingService encoding, IFfmpegLocator ffmpeg)
    {
        _encoding = encoding;
        _ffmpeg = ffmpeg;
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
            return StatusCode(500, new { message = $"Could not load transcode settings: {ex.Message}" });
        }
    }

    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] TranscodeSettingsRequest? request)
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
            plugin.Configuration.Transcode ??= new TranscodeSettings();
            if (request.ResetToEnvironment == true)
            {
                plugin.Configuration.Transcode.HardwareAcceleration = null;
                plugin.Configuration.Transcode.VideoEncoder = null;
                plugin.Configuration.Transcode.VaapiDevice = null;
            }
            else
            {
                plugin.Configuration.Transcode.HardwareAcceleration =
                    FfmpegEncodingService.NormalizeAcceleration(request.HardwareAcceleration, request.VideoEncoder);
                plugin.Configuration.Transcode.VideoEncoder = NormalizeEncoder(request.VideoEncoder);
                plugin.Configuration.Transcode.VaapiDevice = string.IsNullOrWhiteSpace(request.VaapiDevice)
                    ? null
                    : request.VaapiDevice.Trim();
                if (request.RunAheadSeconds.HasValue)
                {
                    plugin.Configuration.Transcode.RunAheadSeconds =
                        TranscodeSettings.ClampRunAheadSeconds(request.RunAheadSeconds.Value);
                }
            }

            plugin.SaveConfiguration();
            _encoding.ApplyFromSaved(plugin.Configuration.Transcode);
            return Ok(BuildPayload());
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Could not save transcode settings: {ex.Message}" });
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult<object>> TestEncode(CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=320x180:r=30:d=1"]);
        args.AddRange(["-vf", _encoding.AdaptVideoFilterForEncoder("format=yuv420p", _encoding.Encoder)]);
        _encoding.AppendVideoEncoder(args, stillImage: true);
        args.AddRange(["-an", "-f", "null", "-"]);

        var stderr = new StringBuilder();
        try
        {
            var result = await Cli.Wrap(_ffmpeg.EncoderPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .ExecuteAsync(cancellationToken);

            var error = stderr.ToString().Trim();
            return Ok(new
            {
                ok = result.ExitCode == 0,
                exitCode = result.ExitCode,
                encoder = _encoding.Encoder,
                ffmpegPath = _ffmpeg.EncoderPath,
                error = error.Length > 2000 ? error[^2000..] : error
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                ok = false,
                exitCode = -1,
                encoder = _encoding.Encoder,
                ffmpegPath = _ffmpeg.EncoderPath,
                error = ex.Message
            });
        }
    }

    private object BuildPayload()
    {
        var saved = FinTvRuntime.Current?.Configuration.Transcode ?? new TranscodeSettings();
        var status = _encoding.Describe();
        var usingSaved = !string.IsNullOrWhiteSpace(saved.HardwareAcceleration)
            || !string.IsNullOrWhiteSpace(saved.VideoEncoder)
            || !string.IsNullOrWhiteSpace(saved.VaapiDevice);
        return new
        {
            hardwareAcceleration = usingSaved
                ? FfmpegEncodingService.NormalizeAcceleration(saved.HardwareAcceleration, saved.VideoEncoder)
                : _encoding.EnvironmentHardwareAcceleration,
            videoEncoder = string.IsNullOrWhiteSpace(saved.VideoEncoder) ? "auto" : saved.VideoEncoder,
            vaapiDevice = FirstNonEmpty(saved.VaapiDevice, status.VaapiDevice, _encoding.EnvironmentVaapiDevice),
            runAheadSeconds = TranscodeSettings.ClampRunAheadSeconds(saved.RunAheadSeconds),
            effectiveEncoder = status.Encoder,
            useVaapi = status.UseVaapi,
            vaapiRequested = status.VaapiRequested,
            vaapiDeviceExists = status.VaapiDeviceExists,
            ffmpegPath = _ffmpeg.EncoderPath,
            source = usingSaved ? "saved" : "environment",
            environment = new
            {
                hardwareAcceleration = _encoding.EnvironmentHardwareAcceleration,
                videoEncoder = _encoding.EnvironmentVideoEncoder,
                vaapiDevice = _encoding.EnvironmentVaapiDevice
            }
        };
    }

    private static string? NormalizeEncoder(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder) || encoder.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return encoder.Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "/dev/dri/renderD128";
    }
}

public class TranscodeSettingsRequest
{
    public string? HardwareAcceleration { get; set; }

    public string? VideoEncoder { get; set; }

    public string? VaapiDevice { get; set; }

    public int? RunAheadSeconds { get; set; }

    public bool? ResetToEnvironment { get; set; }
}
