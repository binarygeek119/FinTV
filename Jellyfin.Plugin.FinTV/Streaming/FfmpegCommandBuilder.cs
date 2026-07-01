using Jellyfin.Plugin.FinTV.Domain;
using System.Globalization;

namespace Jellyfin.Plugin.FinTV.Streaming;

public class FfmpegCommandBuilder
{
    public IReadOnlyList<string> BuildMediaCommand(
        Channel channel,
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string? bugImagePath)
    {
        var (width, height) = GetResolution(channel);
        var vf = BuildVideoFilterChain(channel, width, height, bugImagePath);

        return new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-vf", vf,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-profile:v", "high",
            "-level", "4.1",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k",
            "-ac", "2",
            "-ar", "48000",
            "-f", "mpegts",
            "-mpegts_flags", "+initial_discontinuity",
            "pipe:1"
        };
    }

    public IReadOnlyList<string> BuildMusicCommand(
        Channel channel,
        string audioPath,
        string? albumArtPath)
    {
        var (width, height) = GetResolution(channel);
        var logo = ResolveBugPath(channel);
        var filter = BuildMusicFilter(width, height, logo, albumArtPath, channel.ScanlinesEnabled && channel.AspectRatio == AspectRatioMode.FourThree);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-i", audioPath
        };

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
            "-map", "0:a",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "stillimage",
            "-c:a", "aac",
            "-b:a", "192k",
            "-shortest",
            "-f", "mpegts",
            "pipe:1"
        });

        return args;
    }

    public IReadOnlyList<string> BuildOfflineSlateCommand(Channel channel)
    {
        var (width, height) = GetResolution(channel);
        return new List<string>
        {
            "-hide_banner",
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r=30",
            "-vf", $"drawtext=text='{EscapeDrawText(channel.Name)} - Off Air':fontcolor=white:fontsize=36:x=(w-text_w)/2:y=(h-text_h)/2",
            "-c:v", "libx264",
            "-t", "30",
            "-f", "mpegts",
            "pipe:1"
        };
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

    private static string BuildVideoFilterChain(Channel channel, int width, int height, string? bugImagePath)
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

        var bug = ResolveBugPath(channel) ?? bugImagePath;
        if (!string.IsNullOrWhiteSpace(bug) && File.Exists(bug))
        {
            var overlay = GetBugOverlay(channel, width, height);
            filters.Add($"movie={EscapeMovie(bug)}[bug];[in][bug]overlay={overlay}[out]");
            return string.Join(',', filters).Replace("[in]", "[0:v]").Replace("[out]", string.Empty);
        }

        return string.Join(',', filters);
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
            var path = Path.Combine(Plugin.Instance?.LogosFolder ?? string.Empty, channel.LogoFileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string EscapeMovie(string path) => path.Replace("\\", "/").Replace(":", "\\:");

    private static string EscapeDrawText(string text) => text.Replace("'", "\\'");
}
