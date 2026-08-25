using FinTv.Domain;
using FinTv.Services;
using System.Globalization;
namespace FinTv.Streaming;

public class FfmpegCommandBuilder
{
    private readonly FfmpegEncodingService _encoding;

    public FfmpegCommandBuilder(FfmpegEncodingService encoding)
    {
        _encoding = encoding;
    }

    public IReadOnlyList<string> BuildMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath,
        string? overlayHeadline = null,
        string? alertTickerPath = null,
        string? sourceAspectRatio = null,
        int? sourceWidth = null,
        int? sourceHeight = null,
        bool overlayBug = true,
        bool fadeBugIn = false,
        bool fadeBugOut = false)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, inputPath);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(context.HardwareDecodeArgs);
        args.AddRange(new[]
        {
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath
        });
        AppendMediaVideoGraph(
            args,
            context,
            channel,
            width,
            height,
            bugImagePath,
            overlayHeadline,
            alertTickerPath,
            durationSeconds: durationSeconds,
            sourceAspectRatio: sourceAspectRatio,
            sourceWidth: sourceWidth,
            sourceHeight: sourceHeight,
            overlayBug: overlayBug,
            fadeBugIn: fadeBugIn,
            fadeBugOut: fadeBugOut);
        AppendVideoEncoderArgs(args, context);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-muxdelay", "0",
            "-muxpreload", "0",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildRemoteMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath,
        IReadOnlyList<SponsorSkipRange>? skipRanges = null,
        string? sourceAspectRatio = null,
        int? sourceWidth = null,
        int? sourceHeight = null,
        bool overlayBug = true)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, inputPath);
        var isRemoteInput = inputPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || inputPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var skipExpr = FfmpegSkipCuts.BuildSelectExpression(skipRanges ?? []);

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
        var isPipe = string.Equals(inputPath, "pipe:0", StringComparison.Ordinal);
        if (!isPipe)
        {
            args.AddRange(context.HardwareDecodeArgs);
        }

        if (isPipe)
        {
            args.AddRange(new[]
            {
                "-fflags", "+genpts+discardcorrupt",
                "-probesize", "32768",
                "-analyzeduration", "500000"
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
                "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture)
            });
        }

        args.AddRange(new[] { "-i", inputPath });
        AppendMediaVideoGraph(
            args,
            context,
            channel,
            width,
            height,
            bugImagePath,
            overlayHeadline: null,
            alertTickerPath: null,
            skipExpr,
            durationSeconds,
            sourceAspectRatio,
            sourceWidth,
            sourceHeight,
            overlayBug);
        AppendVideoEncoderArgs(args, context);
        if (!string.IsNullOrEmpty(skipExpr) && !args.Contains("-filter_complex"))
        {
            args.AddRange(new[]
            {
                "-af", $"aselect='{skipExpr}',asetpts=N/SR/TB"
            });
        }

        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000"
        });
        if (isPipe)
        {
            args.AddRange(new[]
            {
                "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture)
            });
        }

        args.AddRange(new[]
        {
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-muxdelay", "0",
            "-muxpreload", "0",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildMusicCommand(
        Channel channel,
        string audioPath,
        string? albumArtPath,
        string? alertTickerPath = null)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, audioPath);
        var logo = channel.BugPlacement == BugPlacementMode.None ? null : ResolveBugPath(channel);
        var filter = _encoding.AdaptFilterComplexForEncoder(
            BuildMusicFilter(width, height, logo, albumArtPath, channel.ScanlinesEnabled && channel.AspectRatio == AspectRatioMode.FourThree, alertTickerPath),
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
        string? audioPath,
        double? durationSeconds = null)
    {
        var fps = captureFps.ToString(CultureInfo.InvariantCulture);
        var hasAudio = !string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath);
        var context = CreateEncodingContext(width, height);
        var vf = _encoding.AdaptVideoFilterForEncoder(
            $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p",
            context.Encoder);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts",
            "-thread_queue_size", "512"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-f", "image2pipe",
            "-vcodec", "mjpeg",
            "-framerate", fps,
            "-i", "pipe:0"
        });

        if (hasAudio)
        {
            args.AddRange(new[]
            {
                "-thread_queue_size", "1024",
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
            "-vf", vf,
            "-map", "0:v:0",
            "-map", "1:a:0?",
            "-af", "aresample=async=1:first_pts=0,aformat=sample_rates=48000:channel_layouts=stereo"
        });
        AppendVideoEncoderArgs(args, context, stillImage: true);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000"
        });
        if (durationSeconds is > 0)
        {
            args.Add("-t");
            args.Add(durationSeconds.Value.ToString("F3", CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-flush_packets", "1",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildWeatherAlertToneCommand(Channel channel, string audioPath)
    {
        var (width, height) = GetResolution(channel);
        var context = CreateEncodingContext(width, height, audioPath);
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts"
        };
        args.AddRange(context.HardwareDeviceArgs);
        args.AddRange(new[]
        {
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r=30",
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-af", "aresample=async=1:first_pts=0,aformat=sample_rates=48000:channel_layouts=stereo"
        });
        AppendVideoEncoderArgs(args, context, stillImage: true);
        args.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-t", "30",
            "-shortest",
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers+initial_discontinuity",
            "-muxdelay", "0",
            "-muxpreload", "0",
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
        var args = new List<string>
        {
            "-hide_banner"
        };
        args.AddRange(_encoding.HardwareDecodeArgs);
        args.AddRange(new[]
        {
            "-i", inputPath,
            "-vf", "blackdetect=d=0.5:pix_th=0.10",
            "-an",
            "-f", "null",
            "-"
        });
        return args;
    }

    private readonly record struct EncodingContext(
        string Encoder,
        IReadOnlyList<string> HardwareDeviceArgs,
        IReadOnlyList<string> HardwareDecodeArgs);

    private EncodingContext CreateEncodingContext(int width, int height, string? mediaPath = null)
    {
        _ = width;
        _ = height;
        _ = mediaPath;
        return new EncodingContext(_encoding.Encoder, _encoding.HardwareDeviceArgs, _encoding.HardwareDecodeArgs);
    }

    private void AppendVideoEncoderArgs(List<string> args, EncodingContext context, bool stillImage = false)
    {
        args.Add("-c:v");
        args.Add(context.Encoder);
        args.AddRange(_encoding.GetVideoEncoderArguments(stillImage));
    }

    private void AppendMediaVideoGraph(
        List<string> args,
        EncodingContext context,
        Channel channel,
        int width,
        int height,
        string? bugImagePath,
        string? overlayHeadline,
        string? alertTickerPath,
        string? skipExpr = null,
        double durationSeconds = 0,
        string? sourceAspectRatio = null,
        int? sourceWidth = null,
        int? sourceHeight = null,
        bool overlayBug = true,
        bool fadeBugIn = false,
        bool fadeBugOut = false)
    {
        var linear = BuildLinearVideoFilters(channel, width, height, overlayHeadline, alertTickerPath);
        if (!string.IsNullOrEmpty(skipExpr))
        {
            linear = $"select='{skipExpr}',setpts=N/FRAME_RATE/TB,{linear}";
        }

        var bug = overlayBug ? ResolveBugFile(channel, bugImagePath) : null;
        if (string.IsNullOrWhiteSpace(bug))
        {
            args.Add("-vf");
            args.Add(_encoding.AdaptVideoFilterForEncoder(linear, context.Encoder));
            return;
        }

        var bugWidth = Math.Clamp(width / 8, 140, 260);
        var position = GetBugOverlay(channel, width, height, sourceAspectRatio, sourceWidth, sourceHeight);
        var alpha = ChannelBugLayout.AlphaFilters(fadeBugIn, fadeBugOut, durationSeconds);
        var graph =
            $"[0:v]{linear}[base];" +
            $"[1:v]format=rgba,scale={bugWidth}:-1:force_original_aspect_ratio=decrease,{alpha}[bug];" +
            $"[base][bug]overlay={position}:format=auto:eof_action=repeat:repeatlast=1[vout]";
        if (!string.IsNullOrEmpty(skipExpr))
        {
            graph += $";[0:a]aselect='{skipExpr}',asetpts=N/SR/TB[aout]";
        }

        args.AddRange(
        [
            "-loop", "1",
            "-framerate", "30",
            "-i", bug,
            "-filter_complex", _encoding.AdaptFilterComplexForEncoder(graph, context.Encoder),
            "-map", "[vout]",
            "-map", string.IsNullOrEmpty(skipExpr) ? "0:a?" : "[aout]",
            "-shortest"
        ]);
    }

    private static string? ResolveBugFile(Channel channel, string? bugImagePath)
    {
        if (channel.BugPlacement == BugPlacementMode.None)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(bugImagePath) && File.Exists(bugImagePath))
        {
            return bugImagePath;
        }

        var resolved = ResolveBugPath(channel);
        return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved) ? resolved : null;
    }

    private static string BuildLinearVideoFilters(
        Channel channel,
        int width,
        int height,
        string? overlayHeadline,
        string? alertTickerPath)
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

        if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
        {
            AppendPastTenseNewsOverlay(filters, width, height, overlayHeadline);
        }

        AppendWeatherAlertTicker(filters, height, alertTickerPath);
        return string.Join(',', filters);
    }

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

    private static bool HasAlertTicker(string? alertTickerPath)
        => !string.IsNullOrWhiteSpace(alertTickerPath) && File.Exists(alertTickerPath);

    private static void AppendWeatherAlertTicker(List<string> filters, int height, string? alertTickerPath)
    {
        if (!HasAlertTicker(alertTickerPath))
        {
            return;
        }

        var barH = Math.Max(52, height / 18);
        var font = Math.Max(22, height / 42);
        var escaped = EscapeFilterPath(alertTickerPath!);
        filters.Add($"drawbox=x=0:y=h-{barH}:w=iw:h={barH}:color=0xc41e3a@0.90:t=fill");
        filters.Add($"drawtext=textfile='{escaped}':expansion=none:fontcolor=white:fontsize={font}:x=w-mod(t*90\\,w+text_w):y=h-{barH}+{(barH - font) / 2}");
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

    private static string BuildMusicFilter(int width, int height, string? logoPath, string? albumArtPath, bool scanlines, string? alertTickerPath)
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
            var logoInput = current == "[tmpv]" ? "2:v" : "1:v";
            baseFilter += $";{current}[{logoInput}]format=rgba,scale=160:-1,colorchannelmixer=aa={ChannelBugLayout.Opacity.ToString(CultureInfo.InvariantCulture)}[logo];{current}[logo]overlay=W-w-40:40:format=auto[vout]";
        }
        else
        {
            baseFilter += $";{current}null[vout]";
        }

        if (scanlines)
        {
            baseFilter = baseFilter.Replace("[vout]", "[vtmp];[vtmp]format=yuv420p,geq=lum='if(not(mod(Y,4)),lum(X,Y)*0.82,lum(X,Y))'[vout]");
        }

        if (HasAlertTicker(alertTickerPath))
        {
            var barH = Math.Max(52, height / 18);
            var font = Math.Max(22, height / 42);
            var escaped = EscapeFilterPath(alertTickerPath!);
            baseFilter = baseFilter.Replace("[vout]", "[vpre]")
                + $";[vpre]drawbox=x=0:y=h-{barH}:w=iw:h={barH}:color=0xc41e3a@0.90:t=fill[vbar]"
                + $";[vbar]drawtext=textfile='{escaped}':expansion=none:fontcolor=white:fontsize={font}:x=w-mod(t*90\\,w+text_w):y=h-{barH}+{(barH - font) / 2}[vout]";
        }

        return baseFilter;
    }

    private static string GetBugOverlay(
        Channel channel,
        int width,
        int height,
        string? sourceAspectRatio,
        int? sourceWidth,
        int? sourceHeight)
        => ChannelBugLayout.OverlayExpression(
            channel.BugPlacement,
            channel.AspectRatio,
            width,
            height,
            sourceAspectRatio,
            sourceWidth,
            sourceHeight);

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
                         Path.Combine(FinTvRuntime.Current?.LogosFolder ?? string.Empty, "binarygeek119"),
                         FinTvRuntime.Current?.BundledLogosFolder ?? string.Empty
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

    private static string EscapeFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string EscapeDrawText(string text)
        => text.Replace("\\", "\\\\")
            .Replace("'", "\u2019")
            .Replace(":", "\\:")
            .Replace("%", "\\%");
}
