using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using System.Globalization;
namespace Jellyfin.Plugin.FinTV.Streaming;

public class FfmpegCommandBuilder
{
    private readonly JellyfinFfmpegEncodingService _encoding;

    public FfmpegCommandBuilder(JellyfinFfmpegEncodingService encoding)
    {
        _encoding = encoding;
    }

    public IReadOnlyList<string> BuildMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath,
        string? overlayHeadline = null)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, inputPath);
        var vf = _encoding.AdaptVideoFilterForEncoder(
            BuildVideoFilterChain(channel, width, height, bugImagePath, overlayHeadline),
            context.Encoder);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-vf", vf
        });
        AppendVideoEncoderArgs(args, context);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-f", "mpegts",
            "-mpegts_flags", "+initial_discontinuity",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildRemoteMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, inputPath);
        var vf = _encoding.AdaptVideoFilterForEncoder(
            BuildVideoFilterChain(channel, width, height, bugImagePath, overlayHeadline: null),
            context.Encoder);
        var isRemoteInput = inputPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || inputPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };

        if (isRemoteInput)
        {
            args.AddRange(new[]
            {
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_delay_max", "5"
            });
        }

        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-vf", vf
        });
        AppendVideoEncoderArgs(args, context);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-f", "mpegts",
            "-mpegts_flags", "+initial_discontinuity",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildMusicCommand(
        Channel channel,
        string audioPath,
        string? albumArtPath)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, audioPath);
        var logo = channel.BugPlacement == BugPlacementMode.None ? null : ResolveBugPath(channel);
        var filter = _encoding.AdaptFilterComplexForEncoder(
            BuildMusicFilter(width, height, logo, albumArtPath, channel.ScanlinesEnabled && channel.AspectRatio == AspectRatioMode.FourThree),
            context.Encoder);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[] { "-i", audioPath });

        if (!string.IsNullOrWhiteSpace(albumArtPath) && File.Exists(albumArtPath))
        {
            args.AddRange(new[] { "-loop", "1", "-i", albumArtPath });
        }

        if (!string.IsNullOrWhiteSpace(logo) && File.Exists(logo))
        {
            args.AddRange(new[] { "-loop", "1", "-i", logo });
        }

        args.AddRange(new[]
        {
            "-filter_complex", filter,
            "-map", "[vout]",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(args, context, stillImage: true);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildEbsCommand(Channel channel, EbsPlaybackPlan plan)
    {
        var (width, height) = GetResolution(channel);
        var duration = Math.Max(30, plan.DurationSeconds).ToString("F0", CultureInfo.InvariantCulture);

        return plan.DisplayMode switch
        {
            EbsDisplayMode.Static => BuildStaticCommand(width, height, duration, plan.AudioMode, plan.MusicPath),
            EbsDisplayMode.ColorBars => BuildColorBarsCommand(width, height, duration, plan.AudioMode, plan.MusicPath),
            EbsDisplayMode.SlateImage => BuildSlateImageCommand(
                width,
                height,
                duration,
                plan.SlateImagePath,
                plan.AudioMode,
                plan.MusicPath),
            _ => BuildColorBarsCommand(width, height, duration, plan.AudioMode, plan.MusicPath)
        };
    }

    public IReadOnlyList<string> BuildEbsCommand(
        Channel channel,
        string slateImagePath,
        string? audioPath,
        double durationSeconds)
    {
        return BuildEbsCommand(channel, new EbsPlaybackPlan
        {
            DisplayMode = EbsDisplayMode.SlateImage,
            AudioMode = string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)
                ? EbsAudioMode.Silence
                : EbsAudioMode.BackgroundMusic,
            SlateImagePath = slateImagePath,
            MusicPath = audioPath,
            DurationSeconds = durationSeconds
        });
    }

    private IReadOnlyList<string> BuildSlateImageCommand(
        int width,
        int height,
        string duration,
        string? slateImagePath,
        EbsAudioMode audioMode,
        string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(slateImagePath) || !File.Exists(slateImagePath))
        {
            return BuildColorBarsCommand(width, height, duration, audioMode, musicPath);
        }

        var context = CreateEncodingContext(width, height, slateImagePath);
        var filter = _encoding.AdaptFilterComplexForEncoder(
            $"[1:v]scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p[vout]",
            context.Encoder);

        if (audioMode == EbsAudioMode.BackgroundMusic
            && !string.IsNullOrWhiteSpace(musicPath)
            && File.Exists(musicPath))
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning"
            };
            args.AddRange(context.HardwareDeviceArgs);
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", musicPath,
                "-loop", "1",
                "-i", slateImagePath,
                "-filter_complex", filter,
                "-map", "[vout]",
                "-map", "0:a"
            });
            AppendVideoEncoderArgs(args, context, stillImage: true);
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k",
                "-t", duration,
                "-shortest",
                "-f", "mpegts",
                "pipe:1"
            });
            return args;
        }

        var silentArgs = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        silentArgs.AddRange(context.HardwareDeviceArgs);
        silentArgs.AddRange(BuildLavfiAudioInput(audioMode));
        silentArgs.AddRange(new[]
        {
            "-loop", "1",
            "-i", slateImagePath,
            "-filter_complex", filter,
            "-map", "[vout]",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(silentArgs, context, stillImage: true);
        silentArgs.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });
        return silentArgs;
    }

    private IReadOnlyList<string> BuildColorBarsCommand(
        int width,
        int height,
        string duration,
        EbsAudioMode audioMode,
        string? musicPath)
    {
        var context = CreateEncodingContext(width, height);

        if (audioMode == EbsAudioMode.BackgroundMusic
            && !string.IsNullOrWhiteSpace(musicPath)
            && File.Exists(musicPath))
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning"
            };
            args.AddRange(context.HardwareDeviceArgs);
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", musicPath,
                "-f", "lavfi",
                "-i", $"smptebars=size={width}x{height}:rate=30",
                "-map", "1:v",
                "-map", "0:a"
            });
            AppendVideoEncoderArgs(args, context, stillImage: true);
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k",
                "-t", duration,
                "-shortest",
                "-f", "mpegts",
                "pipe:1"
            });
            return args;
        }

        var silentArgs = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        silentArgs.AddRange(context.HardwareDeviceArgs);
        silentArgs.AddRange(BuildLavfiAudioInput(audioMode));
        silentArgs.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"smptebars=size={width}x{height}:rate=30",
            "-map", "1:v",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(silentArgs, context, stillImage: true);
        silentArgs.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });
        return silentArgs;
    }

    private IReadOnlyList<string> BuildStaticCommand(
        int width,
        int height,
        string duration,
        EbsAudioMode audioMode,
        string? musicPath)
    {
        var context = CreateEncodingContext(width, height);

        if (audioMode == EbsAudioMode.BackgroundMusic
            && !string.IsNullOrWhiteSpace(musicPath)
            && File.Exists(musicPath))
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning"
            };
            args.AddRange(context.HardwareDeviceArgs);
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", musicPath,
                "-f", "lavfi",
                "-i", $"color=c=808080:s={width}x{height}:r=30,format=gray,geq=lum='255*random(0)'",
                "-map", "1:v",
                "-map", "0:a"
            });
            AppendVideoEncoderArgs(args, context);
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", "192k",
                "-t", duration,
                "-shortest",
                "-f", "mpegts",
                "pipe:1"
            });
            return args;
        }

        var silentArgs = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        silentArgs.AddRange(context.HardwareDeviceArgs);
        silentArgs.AddRange(BuildLavfiAudioInput(audioMode));
        silentArgs.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"color=c=808080:s={width}x{height}:r=30,format=gray,geq=lum='255*random(0)'",
            "-map", "1:v",
            "-map", "0:a"
        });
        AppendVideoEncoderArgs(silentArgs, context);
        silentArgs.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-t", duration,
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });
        return silentArgs;
    }

    private static IReadOnlyList<string> BuildLavfiAudioInput(EbsAudioMode audioMode)
    {
        return audioMode switch
        {
            EbsAudioMode.WhiteNoise => new List<string>
            {
                "-f", "lavfi",
                "-i", "anoisesrc=color=white:amplitude=0.01:sample_rate=48000"
            },
            EbsAudioMode.BeepTone => new List<string>
            {
                "-f", "lavfi",
                "-i", "sine=frequency=960:sample_rate=48000,aeval=val(0)*if(lt(mod(t\\,1)\\,0.5)\\,1\\,0)"
            },
            _ => new List<string>
            {
                "-f", "lavfi",
                "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"
            }
        };
    }

    public IReadOnlyList<string> BuildWeatherCommand(
        int width,
        int height,
        double captureFps,
        string? audioPath)
    {
        var fps = captureFps.ToString(CultureInfo.InvariantCulture);
        var gop = Math.Max(12, (int)Math.Round(captureFps * 2));
        var hasAudio = !string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts",
            "-thread_queue_size", "512",
            "-f", "image2pipe",
            "-vcodec", "mjpeg",
            "-framerate", fps,
            "-i", "pipe:0"
        };

        if (hasAudio)
        {
            args.AddRange(new[]
            {
                "-stream_loop", "-1",
                "-i", audioPath!
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-f", "lavfi",
                "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"
            });
        }

        args.AddRange(new[]
        {
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p",
            "-map", "0:v",
            "-map", "1:a",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-profile:v", "baseline",
            "-level", "3.1",
            "-pix_fmt", "yuv420p",
            "-g", gop.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", Math.Max(1, (int)Math.Round(captureFps)).ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0",
            "-bf", "0",
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildOfflineSlateCommand(Channel channel)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height);
        var args = new List<string>
        {
            "-hide_banner"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r=30",
            "-vf", $"drawtext=text='{EscapeDrawText(channel.Name)} - Off Air':fontcolor=white:fontsize=36:x=(w-text_w)/2:y=(h-text_h)/2"
        });
        AppendVideoEncoderArgs(args, context);
        args.AddRange(new[]
        {
            "-t", "30",
            "-f", "mpegts",
            "pipe:1"
        });
        return args;
    }

    public IReadOnlyList<string> BuildBlackdetectCommand(string inputPath)
    {
        return new List<string>
        {
            "-hide_banner",
            "-i", inputPath,
            "-vf", "blackdetect=d=0.5:pix_th=0.10",
            "-an",
            "-f", "null",
            "-"
        };
    }

    private readonly record struct EncodingContext(
        EncodingJobInfo State,
        EncodingOptions Options,
        string Encoder,
        IReadOnlyList<string> HardwareDeviceArgs);

    private EncodingContext CreateEncodingContext(int width, int height, string? mediaPath = null)
    {
        var options = _encoding.GetEncodingOptions();
        var state = _encoding.CreateVideoEncodingState(width, height, mediaPath);
        var encoder = _encoding.GetH264VideoEncoder(state, options);
        var hardwareDeviceArgs = _encoding.GetHardwareDeviceArguments(state, options);
        return new EncodingContext(state, options, encoder, hardwareDeviceArgs);
    }

    private void AppendVideoEncoderArgs(List<string> args, EncodingContext context, bool stillImage = false)
    {
        args.Add("-c:v");
        args.Add(context.Encoder);
        args.AddRange(_encoding.GetVideoEncoderArguments(context.State, context.Options, context.Encoder, stillImage));
    }

    private static string BuildVideoFilterChain(
        Channel channel,
        int width,
        int height,
        string? bugImagePath,
        string? overlayHeadline)
    {
        var filters = new List<string>
        {
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black"
        };

        if (channel.ScanlinesEnabled && channel.AspectRatio == AspectRatioMode.FourThree)
        {
            filters.Add("format=yuv420p,geq=lum='if(not(mod(Y,4)),lum(X,Y)*0.82,lum(X,Y))'");
        }

        var newsOverlay = IsPastTenseNewsChannel(channel);
        if (newsOverlay)
        {
            AppendPastTenseNewsOverlay(filters, width, height, overlayHeadline);
        }

        var bug = channel.BugPlacement == BugPlacementMode.None
            ? null
            : (!string.IsNullOrWhiteSpace(bugImagePath) && File.Exists(bugImagePath)
                ? bugImagePath
                : ResolveBugPath(channel));
        if (!string.IsNullOrWhiteSpace(bug) && File.Exists(bug))
        {
            var overlay = GetBugOverlay(channel, width, height);
            if (newsOverlay)
            {
                return $"{string.Join(',', filters)}[v];movie={EscapeMovie(bug)}[bug];[v][bug]overlay={overlay}";
            }

            filters.Add($"movie={EscapeMovie(bug)}[bug];[in][bug]overlay={overlay}[out]");
            return string.Join(',', filters).Replace("[in]", "[0:v]").Replace("[out]", string.Empty);
        }

        return string.Join(',', filters);
    }

    private static bool IsPastTenseNewsChannel(Channel channel)
        => FilterDefinition.PresetIdsEqual(
            ChannelAiRules.ExtractLibraryTag(channel.FilterJson),
            "channelflow-past-tense-news");

    private static void AppendPastTenseNewsOverlay(List<string> filters, int width, int height, string? headline)
    {
        _ = width;
        var barH = Math.Max(52, height / 18);
        var lowerH = Math.Max(92, height / 11);
        var font = Math.Max(24, height / 38);
        var small = Math.Max(16, height / 50);
        filters.Add($"drawbox=x=0:y=0:w=iw:h={barH}:color=0xe11d48@0.92:t=fill");
        filters.Add($"drawtext=text='BREAKING NEWS':expansion=none:fontcolor=white:fontsize={font}:x=28:y=({barH}-th)/2");
        filters.Add($"drawbox=x=0:y=h-{lowerH}:w=iw:h={lowerH}:color=0x101010@0.90:t=fill");
        filters.Add($"drawtext=text='PAST TENSE NEWS':expansion=none:fontcolor=0xe11d48:fontsize={small}:x=28:y=h-{lowerH}+10");
        if (string.IsNullOrWhiteSpace(headline))
        {
            return;
        }

        var title = TruncateForDrawText(headline, 72);
        filters.Add($"drawtext=text='{EscapeDrawText(title)}':expansion=none:fontcolor=white:fontsize={font}:x=28:y=h-{lowerH}+{small + 18}");
    }

    private static string TruncateForDrawText(string text, int maxChars)
    {
        var trimmed = text.Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..Math.Max(1, maxChars - 1)].TrimEnd() + "…";
    }

    private static string BuildMusicFilter(int width, int height, string? logoPath, string? albumArtPath, bool scanlines)
    {
        var baseFilter = $"color=c=0x111111:s={width}x{height}:r=30[base]";
        var current = "[base]";

        if (!string.IsNullOrWhiteSpace(albumArtPath) && File.Exists(albumArtPath))
        {
            baseFilter += $";{current}[1:v]scale={width / 2}:{height / 2}:force_original_aspect_ratio=decrease[art];[base][art]overlay=(W-w)/2:(H-h)/2[tmpv]";
            current = "[tmpv]";
        }

        if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
        {
            baseFilter += $";{current}[2:v]scale=160:-1[logo];[tmpv][logo]overlay=W-w-40:40[vout]";
        }
        else
        {
            baseFilter += $";{current}null[vout]";
        }

        if (scanlines)
        {
            baseFilter = baseFilter.Replace("[vout]", "[vtmp];[vtmp]format=yuv420p,geq=lum='if(not(mod(Y,4)),lum(X,Y)*0.82,lum(X,Y))'[vout]");
        }

        return baseFilter;
    }

    private static string GetBugOverlay(Channel channel, int width, int height)
    {
        const int margin = 24;
        return channel.BugPlacement switch
        {
            BugPlacementMode.TopLeft => $"{margin}:{margin}",
            BugPlacementMode.TopRight => $"W-w-{margin}:{margin}",
            BugPlacementMode.BottomLeft => $"{margin}:H-h-{margin}",
            BugPlacementMode.BottomRight => $"W-w-{margin}:H-h-{margin}",
            BugPlacementMode.None => string.Empty,
            BugPlacementMode.Auto => $"W-w-{margin}:{margin}",
            _ => $"W-w-{margin}:{margin}"
        };
    }

    private static (int Width, int Height) GetResolution(Channel channel)
    {
        return channel.AspectRatio == AspectRatioMode.FourThree
            ? (1440, 1080)
            : (1920, 1080);
    }

    private static string? ResolveBugPath(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.ChannelLogoPath) && File.Exists(channel.ChannelLogoPath))
        {
            return channel.ChannelLogoPath;
        }

        if (channel.LogoSetId.HasValue && !string.IsNullOrWhiteSpace(channel.LogoFileName))
        {
            foreach (var logosRoot in new[]
                     {
                         Path.Combine(Plugin.Instance?.LogosFolder ?? string.Empty, "binarygeek119"),
                         Plugin.Instance?.BundledLogosFolder ?? string.Empty
                     }.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
            {
                var found = Directory.EnumerateFiles(logosRoot, channel.LogoFileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string EscapeMovie(string path) => path.Replace("\\", "/").Replace(":", "\\:");

    private static string EscapeDrawText(string text)
        => text.Replace("\\", "\\\\")
            .Replace("'", "\u2019")
            .Replace(":", "\\:")
            .Replace("%", "\\%");
}
