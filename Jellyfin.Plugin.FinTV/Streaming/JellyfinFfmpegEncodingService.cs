using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.FinTV.Streaming;

/// <summary>
/// Applies Jellyfin dashboard transcoding settings (hardware acceleration, encoder preset, VAAPI device, etc.)
/// to FinTV ffmpeg command lines.
/// </summary>
public class JellyfinFfmpegEncodingService
{
    private readonly IConfigurationManager _configurationManager;
    private readonly EncodingHelper _encodingHelper;

    public JellyfinFfmpegEncodingService(
        IConfigurationManager configurationManager,
        EncodingHelper encodingHelper)
    {
        _configurationManager = configurationManager;
        _encodingHelper = encodingHelper;
    }

    public EncodingOptions GetEncodingOptions() => _configurationManager.GetEncodingOptions();

    public EncodingJobInfo CreateVideoEncodingState(int width, int height, string? mediaPath = null)
    {
        return new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            IsVideoRequest = true,
            VideoType = VideoType.VideoFile,
            MediaPath = mediaPath,
            OutputVideoCodec = "h264",
            BaseRequest = new BaseEncodingJobOptions
            {
                Width = width,
                Height = height,
                MaxWidth = width,
                MaxHeight = height,
                VideoCodec = "h264",
                Profile = "high",
            }
        };
    }

    public string GetH264VideoEncoder(EncodingJobInfo state, EncodingOptions options)
        => _encodingHelper.GetH264Encoder(state, options);

    public bool IsHardwareVideoEncoder(string videoEncoder)
        => !string.IsNullOrWhiteSpace(videoEncoder)
           && !EncodingHelper.IsCopyCodec(videoEncoder)
           && !videoEncoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetHardwareDeviceArguments(EncodingJobInfo state, EncodingOptions options)
    {
        if (!ShouldUseHardwareVideoEncoding(options))
        {
            return Array.Empty<string>();
        }

        return ParseFfmpegArgumentString(_encodingHelper.GetInputVideoHwaccelArgs(state, options));
    }

    public IReadOnlyList<string> GetVideoEncoderArguments(
        EncodingJobInfo state,
        EncodingOptions options,
        string videoEncoder,
        bool stillImage = false)
    {
        if (IsHardwareVideoEncoder(videoEncoder))
        {
            return ParseFfmpegArgumentString(
                _encodingHelper.GetVideoQualityParam(state, videoEncoder, options, EncoderPreset.veryfast));
        }

        var args = new List<string>
        {
            "-preset", "veryfast",
            "-profile:v", "high",
            "-level", "4.1",
            "-pix_fmt", "yuv420p"
        };

        if (stillImage)
        {
            args.AddRange(new[] { "-tune", "stillimage" });
        }

        return args;
    }

    public string AdaptVideoFilterForEncoder(string filter, string videoEncoder)
    {
        if (string.IsNullOrWhiteSpace(filter) || !IsHardwareVideoEncoder(videoEncoder))
        {
            return filter;
        }

        if (filter.Contains("yuv420p", StringComparison.OrdinalIgnoreCase))
        {
            return filter.Replace("yuv420p", "nv12", StringComparison.OrdinalIgnoreCase);
        }

        if (filter.Contains("format=", StringComparison.OrdinalIgnoreCase))
        {
            return filter;
        }

        return filter + ",format=nv12";
    }

    public string AdaptFilterComplexForEncoder(string filter, string videoEncoder)
    {
        if (string.IsNullOrWhiteSpace(filter) || !IsHardwareVideoEncoder(videoEncoder))
        {
            return filter;
        }

        if (filter.Contains("yuv420p", StringComparison.OrdinalIgnoreCase))
        {
            return filter.Replace("yuv420p", "nv12", StringComparison.OrdinalIgnoreCase);
        }

        if (filter.Contains("format=nv12", StringComparison.OrdinalIgnoreCase))
        {
            return filter;
        }

        return filter.Replace("[vout]", "format=nv12[vout]", StringComparison.Ordinal);
    }

    private static bool ShouldUseHardwareVideoEncoding(EncodingOptions options)
        => options.EnableHardwareEncoding
           && options.HardwareAccelerationType != HardwareAccelerationType.none;

    private static IReadOnlyList<string> ParseFfmpegArgumentString(string argumentString)
    {
        if (string.IsNullOrWhiteSpace(argumentString))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var text = argumentString.Trim();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            if (text[index] is '\'' or '"')
            {
                var quote = text[index++];
                var start = index;
                while (index < text.Length && text[index] != quote)
                {
                    index++;
                }

                result.Add(text[start..index]);
                if (index < text.Length)
                {
                    index++;
                }

                continue;
            }

            var tokenStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            result.Add(text[tokenStart..index]);
        }

        return result;
    }
}
