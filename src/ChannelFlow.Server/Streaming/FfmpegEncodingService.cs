using FinTv.Configuration;

namespace FinTv.Streaming;

/// <summary>
/// Software or Intel VAAPI H.264 encoding/decoding for MPEG-TS output.
/// </summary>
public class FfmpegEncodingService
{
    private readonly object _gate = new();
    private readonly string _envHardwareAcceleration;
    private readonly string _envVideoEncoder;
    private readonly string _envVaapiDevice;
    private Snapshot _current;

    public string Encoder
    {
        get { lock (_gate) return _current.Encoder; }
    }

    public string? VaapiDevice
    {
        get { lock (_gate) return _current.VaapiDevice; }
    }

    public bool UseVaapi
    {
        get { lock (_gate) return _current.UseVaapi; }
    }

    /// <summary>
    /// Global VAAPI device for the encoder (must appear before inputs).
    /// </summary>
    public IReadOnlyList<string> HardwareDeviceArgs
    {
        get { lock (_gate) return _current.HardwareDeviceArgs; }
    }

    /// <summary>
    /// Hardware decode flags placed immediately before a real video <c>-i</c>.
    /// Frames are downloaded to NV12 so existing software filters (overlay, scanlines) still work.
    /// </summary>
    public IReadOnlyList<string> HardwareDecodeArgs
    {
        get { lock (_gate) return _current.HardwareDecodeArgs; }
    }

    public string EnvironmentHardwareAcceleration => _envHardwareAcceleration;

    public string EnvironmentVideoEncoder => _envVideoEncoder;

    public string EnvironmentVaapiDevice => _envVaapiDevice;

    public FfmpegEncodingService(IConfiguration configuration)
    {
        _envVideoEncoder = FirstNonEmpty(
            configuration["FFMPEG_VIDEO_ENCODER"],
            Environment.GetEnvironmentVariable("FFMPEG_VIDEO_ENCODER"),
            "libx264");
        _envHardwareAcceleration = NormalizeAcceleration(
            configuration["FFMPEG_HWACCEL"] ?? Environment.GetEnvironmentVariable("FFMPEG_HWACCEL"),
            _envVideoEncoder);
        _envVaapiDevice = FirstNonEmpty(
            configuration["FFMPEG_VAAPI_DEVICE"],
            Environment.GetEnvironmentVariable("FFMPEG_VAAPI_DEVICE"),
            "/dev/dri/renderD128");
        _current = BuildSnapshot(_envHardwareAcceleration, _envVideoEncoder, _envVaapiDevice);
    }

    public bool IsHardwareVideoEncoder =>
        !Encoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

    public void ApplyFromSaved(TranscodeSettings? saved)
    {
        Apply(
            FirstNonEmpty(saved?.HardwareAcceleration, _envHardwareAcceleration),
            FirstNonEmpty(saved?.VideoEncoder, _envVideoEncoder),
            FirstNonEmpty(saved?.VaapiDevice, _envVaapiDevice));
    }

    public void Apply(string? hardwareAcceleration, string? videoEncoder, string? vaapiDevice)
    {
        var snapshot = BuildSnapshot(
            NormalizeAcceleration(hardwareAcceleration, videoEncoder),
            string.IsNullOrWhiteSpace(videoEncoder) ? _envVideoEncoder : videoEncoder.Trim(),
            string.IsNullOrWhiteSpace(vaapiDevice) ? _envVaapiDevice : vaapiDevice.Trim());
        lock (_gate)
        {
            _current = snapshot;
        }
    }

    public EncodingStatus Describe()
    {
        lock (_gate)
        {
            return new EncodingStatus(
                _current.HardwareAcceleration,
                _current.Encoder,
                _current.VaapiDevice,
                _current.UseVaapi,
                _current.VaapiDeviceExists,
                _current.VaapiRequested);
        }
    }

    public string AdaptVideoFilterForEncoder(string filter, string videoEncoder)
        => InsertHardwareTail(filter, videoEncoder, labeled: false);

    public string AdaptFilterComplexForEncoder(string filter, string videoEncoder)
        => InsertHardwareTail(filter, videoEncoder, labeled: true);

    private static string InsertHardwareTail(string filter, string videoEncoder, bool labeled)
    {
        if (string.IsNullOrWhiteSpace(filter) || !IsHardware(videoEncoder))
        {
            return filter;
        }

        var adapted = filter.Replace("yuv420p", "nv12", StringComparison.OrdinalIgnoreCase);
        var tail = new List<string>();
        if (!ContainsFilterStep(adapted, "format=nv12") && !ContainsFilterStep(adapted, "format=vaapi"))
        {
            tail.Add("format=nv12");
        }

        if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
            && !ContainsFilterStep(adapted, "hwupload"))
        {
            tail.Add("hwupload=extra_hw_frames=64");
        }

        if (tail.Count == 0)
        {
            return adapted;
        }

        var insert = "," + string.Join(",", tail);
        if (labeled && adapted.Contains("[vout]", StringComparison.Ordinal))
        {
            return adapted.Replace("[vout]", insert + "[vout]", StringComparison.Ordinal);
        }

        return adapted.TrimEnd(',') + insert;
    }

    private static bool ContainsFilterStep(string filter, string step)
        => filter.Contains(step, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetVideoEncoderArguments(bool stillImage)
    {
        if (Encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "-b:v", stillImage ? "1500k" : "4000k",
                "-maxrate", stillImage ? "2500k" : "5000k",
                "-bufsize", stillImage ? "4000k" : "8000k",
                "-profile:v", "main",
                "-level", "4.1",
                "-g", stillImage ? "12" : "30",
                "-bf", "0"
            ];
        }

        if (Encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return ["-preset", "p4", "-b:v", stillImage ? "1500k" : "4000k", "-maxrate", "5000k"];
        }

        return
        [
            "-preset", "veryfast",
            "-tune", stillImage ? "stillimage" : "film",
            "-crf", stillImage ? "23" : "21",
            "-pix_fmt", "yuv420p",
            "-g", stillImage ? "12" : "30",
            "-bf", "0"
        ];
    }

    public void AppendVideoEncoder(List<string> args, bool stillImage = false)
    {
        args.Add("-c:v");
        args.Add(Encoder);
        args.AddRange(GetVideoEncoderArguments(stillImage));
    }

    private static Snapshot BuildSnapshot(string hardwareAcceleration, string requestedEncoder, string vaapiDevice)
    {
        var wantVaapi = hardwareAcceleration == "vaapi"
            || requestedEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
        var wantNvenc = hardwareAcceleration == "nvenc"
            || requestedEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        var deviceExists = !string.IsNullOrWhiteSpace(vaapiDevice) && File.Exists(vaapiDevice);
        var useVaapi = wantVaapi && deviceExists;

        if (useVaapi)
        {
            var encoder = requestedEncoder == "libx264" || string.IsNullOrWhiteSpace(requestedEncoder)
                || requestedEncoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "h264_vaapi"
                : requestedEncoder;
            return new Snapshot(
                "vaapi",
                encoder,
                vaapiDevice,
                true,
                true,
                true,
                ["-vaapi_device", vaapiDevice],
                ["-hwaccel", "vaapi", "-hwaccel_device", vaapiDevice, "-hwaccel_output_format", "nv12"]);
        }

        if (wantNvenc)
        {
            var encoder = requestedEncoder == "libx264" || string.IsNullOrWhiteSpace(requestedEncoder)
                || requestedEncoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "h264_nvenc"
                : requestedEncoder;
            return new Snapshot(
                "nvenc",
                encoder,
                vaapiDevice,
                false,
                deviceExists,
                wantVaapi,
                ["-hwaccel", "cuda"],
                ["-hwaccel", "cuda"]);
        }

        var softwareEncoder = string.IsNullOrWhiteSpace(requestedEncoder)
            || requestedEncoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || requestedEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
            || requestedEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
            ? "libx264"
            : requestedEncoder;
        return new Snapshot(
            "none",
            softwareEncoder,
            vaapiDevice,
            false,
            deviceExists,
            wantVaapi,
            [],
            []);
    }

    internal static string NormalizeAcceleration(string? hardwareAcceleration, string? videoEncoder)
    {
        var value = (hardwareAcceleration ?? string.Empty).Trim().ToLowerInvariant();
        if (value is "vaapi" or "nvenc" or "none" or "off" or "software" or "libx264")
        {
            return value is "off" or "software" or "libx264" ? "none" : value;
        }

        if (!string.IsNullOrWhiteSpace(videoEncoder)
            && videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
        {
            return "vaapi";
        }

        if (!string.IsNullOrWhiteSpace(videoEncoder)
            && videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return "nvenc";
        }

        return "none";
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

        return string.Empty;
    }

    private static bool IsHardware(string encoder)
        => !string.IsNullOrWhiteSpace(encoder)
           && !encoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

    public sealed record EncodingStatus(
        string HardwareAcceleration,
        string Encoder,
        string? VaapiDevice,
        bool UseVaapi,
        bool VaapiDeviceExists,
        bool VaapiRequested);

    private sealed record Snapshot(
        string HardwareAcceleration,
        string Encoder,
        string? VaapiDevice,
        bool UseVaapi,
        bool VaapiDeviceExists,
        bool VaapiRequested,
        IReadOnlyList<string> HardwareDeviceArgs,
        IReadOnlyList<string> HardwareDecodeArgs);
}
