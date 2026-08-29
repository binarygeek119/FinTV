using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChannelFlow.CommercialDetect;

public static partial class FfmpegTools
{
    [GeneratedRegex(@"black_start:(?<start>[-+]?\d*\.?\d+)(?:\s+black_end:(?<end>[-+]?\d*\.?\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex BlackRegex();

    [GeneratedRegex(@"silence_start:\s*(?<start>[-+]?\d*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(?<end>[-+]?\d*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SilenceEndRegex();

    public static string ResolveFfprobe(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
    }

    public static async Task<MediaProbe> ProbeAsync(
        string ffprobePath,
        string videoPath,
        CancellationToken cancellationToken)
    {
        var json = await RunAsync(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "-i", videoPath],
            captureStdout: true,
            throwOnError: false,
            cancellationToken).ConfigureAwait(false);

        var duration = 0d;
        var fps = 30d;
        var chapters = new List<FileChapter>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.TryGetProperty("format", out var format))
            {
                duration = ReadDouble(format, "duration") ?? 0;
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!string.Equals(ReadString(stream, "codec_type"), "video", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    fps = ParseFrameRate(ReadString(stream, "avg_frame_rate"))
                        ?? ParseFrameRate(ReadString(stream, "r_frame_rate"))
                        ?? fps;
                    break;
                }
            }

            if (doc.RootElement.TryGetProperty("chapters", out var chapterEl) && chapterEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var chapter in chapterEl.EnumerateArray())
                {
                    var start = ReadDouble(chapter, "start_time") ?? 0;
                    var end = ReadDouble(chapter, "end_time") ?? start;
                    string? name = null;
                    if (chapter.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                    {
                        name = ReadString(tags, "title") ?? ReadString(tags, "TITLE");
                    }

                    chapters.Add(new FileChapter
                    {
                        StartSeconds = start,
                        EndSeconds = end < start ? start : end,
                        Name = name?.Trim() ?? string.Empty
                    });
                }
            }
        }
        catch (JsonException)
        {
        }

        return new MediaProbe(duration, fps, chapters);
    }

    public static async Task<(List<TimeRange> Black, List<TimeRange> Silence)> DetectAsync(
        string ffmpegPath,
        string videoPath,
        CommercialBreakScanSettings settings,
        double framesPerSecond,
        CancellationToken cancellationToken)
    {
        settings.Clamp();
        var blackMin = settings.BlackMinSeconds(framesPerSecond).ToString("0.###", CultureInfo.InvariantCulture);
        var pix = settings.BlackPixThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        var pic = settings.BlackPictureRatio.ToString("0.###", CultureInfo.InvariantCulture);
        var noise = settings.SilenceDb.ToString("0.#", CultureInfo.InvariantCulture) + "dB";
        var silenceMin = settings.SilenceMinSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var args = new[]
        {
            "-hide_banner",
            "-nostats",
            "-i", videoPath,
            "-vf", $"blackdetect=d={blackMin}:pix_th={pix}:pic_th={pic}",
            "-af", $"silencedetect=noise={noise}:d={silenceMin}",
            "-f", "null",
            "-"
        };

        var stderr = await RunAsync(
            ffmpegPath,
            args,
            captureStdout: false,
            throwOnError: false,
            cancellationToken).ConfigureAwait(false);
        return (ParseBlack(stderr), ParseSilence(stderr));
    }

    public static bool CanWriteInPlace(string videoPath)
    {
        try
        {
            var info = new FileInfo(videoPath);
            if (!info.Exists || info.IsReadOnly)
            {
                return false;
            }

            using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    public static bool SupportsChapterRewrite(string videoPath)
    {
        var ext = Path.GetExtension(videoPath);
        return ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task WriteChaptersAsync(
        string ffmpegPath,
        string videoPath,
        IReadOnlyList<FileChapter> chapters,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? Path.GetTempPath();
        var ext = Path.GetExtension(videoPath);
        var metaPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + ".channelflow-chapters.txt");
        var tempPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + ".channelflow-tmp" + ext);
        await File.WriteAllTextAsync(metaPath, ChapterFfmetadata.Build(chapters), cancellationToken).ConfigureAwait(false);
        try
        {
            await RunAsync(
                ffmpegPath,
                ["-y", "-i", videoPath, "-i", metaPath, "-map_metadata", "1", "-map", "0", "-c", "copy", tempPath],
                captureStdout: false,
                throwOnError: true,
                cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, videoPath, overwrite: true);
        }
        finally
        {
            TryDelete(metaPath);
            TryDelete(tempPath);
        }
    }

    private static List<TimeRange> ParseBlack(string log)
    {
        var list = new List<TimeRange>();
        foreach (Match match in BlackRegex().Matches(log))
        {
            if (!TryDouble(match.Groups["start"].Value, out var start))
            {
                continue;
            }

            var end = TryDouble(match.Groups["end"].Value, out var parsedEnd) ? parsedEnd : start;
            if (end < start)
            {
                end = start;
            }

            list.Add(new TimeRange(start, end));
        }

        return list;
    }

    private static List<TimeRange> ParseSilence(string log)
    {
        var list = new List<TimeRange>();
        double? open = null;
        foreach (var line in log.Split('\n'))
        {
            var startMatch = SilenceStartRegex().Match(line);
            if (startMatch.Success && TryDouble(startMatch.Groups["start"].Value, out var start))
            {
                open = start;
                continue;
            }

            var endMatch = SilenceEndRegex().Match(line);
            if (endMatch.Success && TryDouble(endMatch.Groups["end"].Value, out var end) && open is double began)
            {
                list.Add(new TimeRange(began, Math.Max(began, end)));
                open = null;
            }
        }

        return list;
    }

    private static async Task<string> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        bool captureStdout,
        bool throwOnError,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return string.Empty;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return captureStdout ? stdout.ToString() : stderr.ToString();
        }

        var output = captureStdout ? stdout.ToString() : stderr.ToString();
        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "ffmpeg exited " + process.ExitCode + (string.IsNullOrWhiteSpace(output) ? "" : ": " + TrimLog(output)));
        }

        return output;
    }

    private static string TrimLog(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 800 ? trimmed : trimmed[^800..];
    }

    private static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0")
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den != 0)
        {
            return num / den;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ? fps : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryDouble(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record MediaProbe(double DurationSeconds, double FramesPerSecond, IReadOnlyList<FileChapter> Chapters);
