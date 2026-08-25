using System.Text;
using CliWrap;
using FinTv;
using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Services;
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
    private readonly GpuCapabilityService _gpu;
    private readonly StreamNormalizationService _normalization;
    private readonly FfmpegCommandBuilder _commands;
    private readonly IFfmpegLocator _ffmpeg;

    public TranscodeController(
        FfmpegEncodingService encoding,
        GpuCapabilityService gpu,
        StreamNormalizationService normalization,
        FfmpegCommandBuilder commands,
        IFfmpegLocator ffmpeg)
    {
        _encoding = encoding;
        _gpu = gpu;
        _normalization = normalization;
        _commands = commands;
        _ffmpeg = ffmpeg;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<object>> GetSettings(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await BuildPayloadAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not load transcode settings: {ex.Message}" });
        }
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] TranscodeSettingsRequest? request, CancellationToken cancellationToken)
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
            var previousDevice = _encoding.VaapiDevice;
            if (request.ResetToEnvironment == true)
            {
                plugin.Configuration.Transcode.HardwareAcceleration = null;
                plugin.Configuration.Transcode.VideoEncoder = null;
                plugin.Configuration.Transcode.VaapiDevice = null;
            }
            else
            {
                await _gpu.GetAsync(cancellationToken);
                var accel = _gpu.ClampAcceleration(request.HardwareAcceleration, request.VideoEncoder);
                plugin.Configuration.Transcode.HardwareAcceleration = accel;
                plugin.Configuration.Transcode.VideoEncoder = NormalizeEncoder(_gpu.ClampEncoder(request.VideoEncoder, accel));
                plugin.Configuration.Transcode.VaapiDevice = accel == "vaapi"
                    ? _gpu.ClampVaapiDevice(request.VaapiDevice)
                    : string.IsNullOrWhiteSpace(request.VaapiDevice) ? null : request.VaapiDevice.Trim();
                if (request.RunAheadSeconds.HasValue)
                {
                    plugin.Configuration.Transcode.RunAheadSeconds =
                        TranscodeSettings.ClampRunAheadSeconds(request.RunAheadSeconds.Value);
                }
            }

            plugin.SaveConfiguration();
            var deviceChanged = !string.Equals(
                previousDevice,
                plugin.Configuration.Transcode.VaapiDevice,
                StringComparison.Ordinal);
            if (request.ResetToEnvironment == true || deviceChanged)
            {
                _gpu.Invalidate();
            }

            await _gpu.GetAsync(cancellationToken);
            _encoding.ApplyFromSaved(plugin.Configuration.Transcode);
            plugin.Configuration.Normalization = _gpu.ClampNormalization(
                plugin.Configuration.Normalization ?? NormalizationSettings.CreateDefault(),
                _encoding.Describe().HardwareAcceleration);
            plugin.SaveConfiguration();
            _normalization.ApplyFromSaved(plugin.Configuration.Normalization);
            return Ok(await BuildPayloadAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Could not save transcode settings: {ex.Message}" });
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult<object>> TestEncode(CancellationToken cancellationToken)
    {
        var args = _commands.BuildTestEncodeCommand().ToList();
        var stderr = new StringBuilder();
        try
        {
            var result = await Cli.Wrap(_ffmpeg.EncoderPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                .ExecuteAsync(cancellationToken);

            var pipeline = _commands.DescribePipeline();
            var error = stderr.ToString().Trim();
            return Ok(new
            {
                ok = result.ExitCode == 0,
                exitCode = result.ExitCode,
                encoder = pipeline.Encoder,
                summary = pipeline.Summary,
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
                encoder = _encoding.ResolveVideoEncoder(_normalization.Current.IsMpeg2),
                summary = _commands.DescribePipeline().Summary,
                ffmpegPath = _ffmpeg.EncoderPath,
                error = ex.Message
            });
        }
    }

    private async Task<object> BuildPayloadAsync(CancellationToken cancellationToken)
    {
        var caps = await _gpu.GetAsync(cancellationToken);
        var saved = FinTvRuntime.Current?.Configuration.Transcode ?? new TranscodeSettings();
        var status = _encoding.Describe();
        var usingSaved = !string.IsNullOrWhiteSpace(saved.HardwareAcceleration)
            || !string.IsNullOrWhiteSpace(saved.VideoEncoder)
            || !string.IsNullOrWhiteSpace(saved.VaapiDevice);
        var requestedAccel = usingSaved
            ? FfmpegEncodingService.NormalizeAcceleration(saved.HardwareAcceleration, saved.VideoEncoder)
            : _encoding.EnvironmentHardwareAcceleration;
        var accel = _gpu.ClampAcceleration(requestedAccel, saved.VideoEncoder);
        var encoder = usingSaved
            ? _gpu.ClampEncoder(string.IsNullOrWhiteSpace(saved.VideoEncoder) ? "auto" : saved.VideoEncoder, accel)
            : "auto";
        var vaapiDevice = accel == "vaapi"
            ? _gpu.ClampVaapiDevice(FirstNonEmpty(saved.VaapiDevice, status.VaapiDevice, _encoding.EnvironmentVaapiDevice))
            : FirstNonEmpty(saved.VaapiDevice, status.VaapiDevice, _encoding.EnvironmentVaapiDevice);
        return new
        {
            hardwareAcceleration = accel,
            videoEncoder = encoder,
            vaapiDevice,
            runAheadSeconds = StreamService.GetRunAheadSeconds(),
            effectiveEncoder = _encoding.ResolveVideoEncoder(_normalization.Current.IsMpeg2),
            pipeline = _commands.DescribePipeline(),
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
            },
            capabilities = new
            {
                summary = caps.Summary,
                driver = caps.Driver,
                accelerations = caps.Accelerations.Select(item => new
                {
                    value = item.Id,
                    label = item.Label,
                    encoders = item.Encoders,
                    devices = item.Devices
                }),
                formats = caps.Formats
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
