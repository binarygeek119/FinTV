using FinTv.Configuration;

namespace FinTv.Streaming;

/// <summary>
/// Software, Intel VAAPI, Intel QSV, or NVENC encoding/decoding for MPEG-TS output.
/// </summary>
public class FfmpegEncodingService
{
    private readonly object _gate = new();
    private readonly GpuCapabilityService _gpu;
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

    public bool UseQsv
    {
        get { lock (_gate) return _current.UseQsv; }
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
    /// Output format is left unset so ffmpeg downloads to system memory when
    /// CPU filters still run. VAAPI scale/pad/overlay uses a separate decode
    /// arg list that keeps frames on the GPU.
    /// </summary>
    public IReadOnlyList<string> HardwareDecodeArgs
    {
        get { lock (_gate) return _current.HardwareDecodeArgs; }
    }

    /// <summary>
    /// Hardware decode flags for this source. VAAPI decode is skipped when the
    /// codec is known-unsafe (AV1 on Intel iHD) or the render node has no VLD
    /// profile for it. QSV decode is allowed for AV1 (<c>av1_qsv</c>). When
    /// <paramref name="stayOnGpu"/> is true, frames stay on the GPU for
    /// scale/pad/overlay; otherwise ffmpeg downloads for CPU filters.
    /// </summary>
    public IReadOnlyList<string> DecodeArgsForSource(string? sourceVideoCodec, bool stayOnGpu = false)
    {
        if (UseQsv)
        {
            if (!_gpu.CanQsvDecode(VaapiDevice, sourceVideoCodec))
            {
                return [];
            }

            return BuildQsvDecodeArgs(sourceVideoCodec, stayOnGpu);
        }

        if (!UseVaapi)
        {
            return HardwareDecodeArgs;
        }

        if (IsUnsafeVaapiDecodeCodec(sourceVideoCodec)
            || !_gpu.CanVaapiDecode(VaapiDevice, sourceVideoCodec))
        {
            return [];
        }

        if (stayOnGpu)
        {
            return ["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"];
        }

        return HardwareDecodeArgs;
    }

    public static string? QsvDecoderFor(string? sourceVideoCodec)
        => NormalizeVideoCodec(sourceVideoCodec) switch
        {
            "h264" => "h264_qsv",
            "hevc" => "hevc_qsv",
            "mpeg2video" => "mpeg2_qsv",
            "vc1" => "vc1_qsv",
            "vp9" => "vp9_qsv",
            "vp8" => "vp8_qsv",
            "av1" => "av1_qsv",
            "mjpeg" => "mjpeg_qsv",
            _ => null
        };

    private static IReadOnlyList<string> BuildQsvDecodeArgs(string? sourceVideoCodec, bool stayOnGpu)
    {
        var args = new List<string> { "-hwaccel", "qsv", "-extra_hw_frames", "64" };
        if (stayOnGpu)
        {
            args.AddRange(["-hwaccel_output_format", "qsv"]);
        }

        var decoder = QsvDecoderFor(sourceVideoCodec);
        if (decoder is not null)
        {
            args.AddRange(["-c:v", decoder]);
        }

        return args;
    }

    public static string? NormalizeVideoCodec(string? sourceVideoCodec)
    {
        if (string.IsNullOrWhiteSpace(sourceVideoCodec))
        {
            return null;
        }

        var codec = sourceVideoCodec.Trim();
        var slash = codec.IndexOf('/');
        if (slash > 0)
        {
            codec = codec[..slash];
        }

        codec = codec.Trim().ToLowerInvariant();
        return codec switch
        {
            "avc" or "avc1" or "h264" or "h.264" => "h264",
            "hevc" or "h265" or "h.265" or "hev1" or "hvc1" => "hevc",
            "mpeg2" or "mpeg2video" or "mp2v" => "mpeg2video",
            "mpeg1" or "mpeg1video" => "mpeg1video",
            "mpeg4" or "mp4v" or "xvid" => "mpeg4",
            "vp9" or "vp9.0" or "libvpx-vp9" => "vp9",
            "vp8" or "libvpx" => "vp8",
            "av1" or "av01" or "libaom-av1" or "libdav1d" or "av1_qsv" or "av1_cuvid" => "av1",
            "vc1" or "wmv3" => "vc1",
            "mjpeg" or "mjpeg_vaapi" => "mjpeg",
            "h263" => "h263",
            _ => codec
        };
    }

    public static bool IsUnsafeVaapiDecodeCodec(string? sourceVideoCodec)
        => NormalizeVideoCodec(sourceVideoCodec) == "av1";

    public string EnvironmentHardwareAcceleration => _envHardwareAcceleration;

    public string EnvironmentVideoEncoder => _envVideoEncoder;

    public string EnvironmentVaapiDevice => _envVaapiDevice;

    public FfmpegEncodingService(IConfiguration configuration, GpuCapabilityService gpu)
    {
        _gpu = gpu;
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
                _current.UseQsv,
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
        if (!ContainsFilterStep(adapted, "format=nv12")
            && !ContainsFilterStep(adapted, "format=vaapi")
            && !ContainsFilterStep(adapted, "format=qsv"))
        {
            tail.Add("format=nv12");
        }

        if (!ContainsFilterStep(adapted, "hwupload"))
        {
            if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
            {
                tail.Add("hwupload=extra_hw_frames=64");
            }
            else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            {
                tail.Add("hwupload=extra_hw_frames=64");
                tail.Add("format=qsv");
            }
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
        => GetVideoEncoderArguments(stillImage, "main", "4.1", "30", stillImage ? 12 : 30, "auto");

    public IReadOnlyList<string> GetVideoEncoderArguments(
        bool stillImage,
        string profile,
        string level,
        string frameRate,
        int gop,
        string videoBitrate)
    {
        var safeProfile = profile is "baseline" or "high" ? profile : "main";
        var safeLevel = string.IsNullOrWhiteSpace(level) ? "4.1" : level;
        var safeRate = string.IsNullOrWhiteSpace(frameRate) ? "30" : frameRate;
        var keyint = Math.Max(1, stillImage ? Math.Min(12, gop) : gop);
        var bitrate = videoBitrate is "2000k" or "4000k" or "6000k" or "8000k" ? videoBitrate : null;
        var maxrate = bitrate ?? (stillImage ? "2500k" : "5000k");
        var bufsize = bitrate is null
            ? (stillImage ? "4000k" : "10000k")
            : DoubleBitrate(bitrate);

        if (Encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
            || Encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            // Do not force -profile:v. Intel iHD often rejects an explicit Main/High
            // profile on real overlay/hwupload surfaces even when a lavfi probe of
            // the same profile succeeds.
            var intel =
                new List<string>
                {
                    "-b:v", bitrate ?? (stillImage ? "1500k" : "4000k"),
                    "-maxrate", bitrate ?? (stillImage ? "2500k" : "5000k"),
                    "-bufsize", bufsize,
                    "-r", safeRate,
                    "-g", keyint.ToString(),
                    "-bf", "0"
                };
            if (Encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            {
                intel.InsertRange(0, ["-preset", stillImage ? "fast" : "veryfast", "-look_ahead", "0"]);
            }

            return intel;
        }

        if (Encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "-preset", "p4",
                "-profile:v", safeProfile,
                "-level", safeLevel,
                "-b:v", bitrate ?? (stillImage ? "1500k" : "4000k"),
                "-maxrate", bitrate ?? "5000k",
                "-r", safeRate,
                "-g", keyint.ToString(),
                "-bf", "0",
                "-pix_fmt", "yuv420p"
            ];
        }

        var software = new List<string>
        {
            "-preset", "veryfast",
            "-tune", stillImage ? "stillimage" : "film",
            "-profile:v", safeProfile,
            "-level", safeLevel
        };
        if (bitrate is null)
        {
            software.AddRange(["-crf", stillImage ? "23" : "21"]);
        }
        else
        {
            software.AddRange(["-b:v", bitrate]);
        }

        software.AddRange(
        [
            "-maxrate", maxrate,
            "-bufsize", bufsize,
            "-pix_fmt", "yuv420p",
            "-r", safeRate,
            "-g", keyint.ToString(),
            "-bf", "0",
            "-sc_threshold", "0"
        ]);
        return software;
    }

    private static string DoubleBitrate(string bitrate)
    {
        if (bitrate.EndsWith('k') && int.TryParse(bitrate[..^1], out var kbps))
        {
            return $"{kbps * 2}k";
        }

        return "8000k";
    }

    public string ResolveVideoEncoder(bool mpeg2)
    {
        if (!mpeg2)
        {
            return Encoder;
        }

        if (UseQsv && _gpu.SupportsMpeg2Qsv())
        {
            return "mpeg2_qsv";
        }

        return UseVaapi && _gpu.SupportsMpeg2Vaapi() ? "mpeg2_vaapi" : "mpeg2video";
    }

    public void AppendVideoEncoder(List<string> args, bool stillImage = false)
    {
        args.Add("-c:v");
        args.Add(Encoder);
        args.AddRange(GetVideoEncoderArguments(stillImage));
    }

    private Snapshot BuildSnapshot(string hardwareAcceleration, string requestedEncoder, string vaapiDevice)
    {
        var wantVaapi = hardwareAcceleration == "vaapi"
            || requestedEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
        var wantQsv = hardwareAcceleration == "qsv"
            || requestedEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase);
        var wantNvenc = hardwareAcceleration == "nvenc"
            || requestedEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        var encodeDevice = _gpu.ClampVaapiDevice(vaapiDevice);
        var deviceExists = !string.IsNullOrWhiteSpace(encodeDevice) && File.Exists(encodeDevice);
        var useVaapi = wantVaapi && !wantQsv && deviceExists;
        var useQsv = wantQsv && deviceExists;

        if (useQsv)
        {
            var encoder = requestedEncoder == "libx264" || string.IsNullOrWhiteSpace(requestedEncoder)
                || requestedEncoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "h264_qsv"
                : requestedEncoder;
            return new Snapshot(
                "qsv",
                encoder,
                encodeDevice,
                false,
                true,
                true,
                wantVaapi,
                [
                    "-init_hw_device", $"vaapi=va:{encodeDevice}",
                    "-init_hw_device", "qsv=hw@va",
                    "-filter_hw_device", "hw"
                ],
                ["-hwaccel", "qsv", "-extra_hw_frames", "64"]);
        }

        if (useVaapi)
        {
            var encoder = requestedEncoder == "libx264" || string.IsNullOrWhiteSpace(requestedEncoder)
                || requestedEncoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "h264_vaapi"
                : requestedEncoder;
            return new Snapshot(
                "vaapi",
                encoder,
                encodeDevice,
                true,
                false,
                true,
                true,
                ["-init_hw_device", $"vaapi=va:{encodeDevice}", "-filter_hw_device", "va"],
                ["-hwaccel", "vaapi", "-hwaccel_device", "va"]);
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
                encodeDevice,
                false,
                false,
                deviceExists,
                wantVaapi,
                ["-hwaccel", "cuda"],
                ["-hwaccel", "cuda"]);
        }

        var softwareEncoder = string.IsNullOrWhiteSpace(requestedEncoder)
            || requestedEncoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || requestedEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
            || requestedEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase)
            || requestedEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
            ? "libx264"
            : requestedEncoder;
        return new Snapshot(
            "none",
            softwareEncoder,
            encodeDevice,
            false,
            false,
            deviceExists,
            wantVaapi || wantQsv,
            [],
            []);
    }

    internal static string NormalizeAcceleration(string? hardwareAcceleration, string? videoEncoder)
    {
        var value = (hardwareAcceleration ?? string.Empty).Trim().ToLowerInvariant();
        if (value is "vaapi" or "qsv" or "nvenc" or "none" or "off" or "software" or "libx264"
            or "quicksync")
        {
            return value is "off" or "software" or "libx264"
                ? "none"
                : value is "quicksync" ? "qsv" : value;
        }

        if (!string.IsNullOrWhiteSpace(videoEncoder)
            && videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            return "qsv";
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
           && (encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
               || encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase)
               || encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase));

    public sealed record EncodingStatus(
        string HardwareAcceleration,
        string Encoder,
        string? VaapiDevice,
        bool UseVaapi,
        bool UseQsv,
        bool VaapiDeviceExists,
        bool VaapiRequested);

    private sealed record Snapshot(
        string HardwareAcceleration,
        string Encoder,
        string? VaapiDevice,
        bool UseVaapi,
        bool UseQsv,
        bool VaapiDeviceExists,
        bool VaapiRequested,
        IReadOnlyList<string> HardwareDeviceArgs,
        IReadOnlyList<string> HardwareDecodeArgs);
}
