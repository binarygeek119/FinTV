using System.Globalization;
using System.Text;

namespace FinTv.News;

internal sealed record NewsStoryBeat(
    double StartSeconds,
    double EndSeconds,
    string Title,
    string Body,
    string? ImagePath,
    bool ShowOnScreen);

internal sealed record NewsImageWindow(string Path, double Start, double End);

public static class NewsAssBuilder
{
    internal static string BuildSpoken(
        int width,
        int height,
        IReadOnlyList<NewsStoryBeat> beats,
        string? presenter = null,
        double presenterStartSeconds = 0,
        double? presenterEndSeconds = null)
    {
        var playX = width;
        var playY = height;
        var events = new StringBuilder();
        var end = FormatAssTime(beats.Count == 0 ? 1 : beats[^1].EndSeconds);

        foreach (var beat in beats)
        {
            if (!beat.ShowOnScreen || beat.EndSeconds <= beat.StartSeconds)
            {
                continue;
            }

            var start = FormatAssTime(beat.StartSeconds);
            var stop = FormatAssTime(beat.EndSeconds);
            if (!string.IsNullOrWhiteSpace(beat.ImagePath))
            {
                if (!string.IsNullOrWhiteSpace(beat.Title))
                {
                    events.Append("Dialogue: 0,")
                        .Append(start).Append(',').Append(stop)
                        .Append(",Caption,,0,0,0,,")
                        .Append(@"{\b1}").Append(Escape(beat.Title)).AppendLine(@"{\b0}");
                }

                continue;
            }

            var wrapped = BuildSpokenBlock(beat.Title, beat.Body, width);
            var lineCount = wrapped.Split("\\N", StringSplitOptions.None).Length;
            var shouldScroll = lineCount > 7;
            string text;
            if (shouldScroll)
            {
                var blockHeight = lineCount * 36 + 40;
                var y1 = playY + 20;
                var y2 = 70 - blockHeight;
                var x = playX / 2;
                text = $"{{\\move({x},{y1},{x},{y2})}}" + wrapped;
            }
            else
            {
                text = wrapped;
            }

            events.Append("Dialogue: 0,")
                .Append(start).Append(',').Append(stop)
                .Append(shouldScroll ? ",Scroll,,0,0,0,," : ",Story,,0,0,0,,")
                .AppendLine(text);
        }

        if (!string.IsNullOrWhiteSpace(presenter))
        {
            var presenterEnd = presenterEndSeconds ?? (beats.Count == 0 ? 1 : beats[^1].EndSeconds);
            events.Append("Dialogue: 1,")
                .Append(FormatAssTime(presenterStartSeconds)).Append(',')
                .Append(FormatAssTime(presenterEnd))
                .Append(",Presenter,,0,0,0,,")
                .Append(Escape(presenter.Trim()))
                .AppendLine();
        }

        if (events.Length == 0)
        {
            events.Append("Dialogue: 0,0:00:00.00,").Append(end)
                .AppendLine(",Story,,0,0,0,,{\\b1}Add RSS feeds on the News tab.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("Title: FlowWire News");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("PlayResX: " + playX);
        sb.AppendLine("PlayResY: " + playY);
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine("Style: Story, Arial, 28, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 0, 5, 48, 48, 40, 1");
        sb.AppendLine("Style: Scroll, Arial, 28, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 0, 8, 48, 48, 36, 1");
        sb.AppendLine("Style: Caption, Arial, 26, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 1, 2, 36, 36, 28, 1");
        sb.AppendLine("Style: Presenter, Arial, 18, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 1, 1, 36, 36, 24, 1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        sb.Append(events);
        return sb.ToString();
    }

    public static string EscapeAssFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string BuildSpokenBlock(string title, string body, int width)
    {
        var maxChars = width > 700 ? 44 : 30;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append(@"{\b1\c&HFFFFFF&}").Append(Escape(title)).Append(@"{\b0}");
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            if (sb.Length > 0)
            {
                sb.Append(@"\N\N");
            }

            sb.Append(@"{\c&HCCCCCC&}");
            var first = true;
            foreach (var line in Wrap(body, maxChars))
            {
                if (!first)
                {
                    sb.Append(@"\N");
                }

                sb.Append(Escape(line));
                first = false;
            }
        }

        return sb.Length == 0 ? @"{\b1}FlowWire News" : sb.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int maxChars)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > maxChars)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("{", "(").Replace("}", ")").Replace("\r", "").Replace("\n", " ");

    private static string FormatAssTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var cs = span.Milliseconds / 10;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:00}:{2:00}.{3:00}",
            (int)span.TotalHours,
            span.Minutes,
            span.Seconds,
            cs);
    }
}
