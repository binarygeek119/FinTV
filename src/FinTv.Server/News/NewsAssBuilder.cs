using System.Globalization;
using System.Text;

namespace FinTv.News;

internal sealed record NewsStoryBeat(
    double StartSeconds,
    double EndSeconds,
    string Title,
    string Body,
    string? ImagePath,
    bool ShowOnScreen,
    bool AnchorPortrait = false);

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
            if (!string.IsNullOrWhiteSpace(beat.Title))
            {
                events.Append("Dialogue: 0,")
                    .Append(start).Append(',').Append(stop)
                    .Append(",Headline,,0,0,0,,")
                    .Append(@"{\b1}").Append(Escape(beat.Title)).AppendLine(@"{\b0}");
            }

            var caption = CaptionLine(beat.Body, beat.Title);
            if (!string.IsNullOrWhiteSpace(caption)
                && !string.Equals(caption, beat.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                events.Append("Dialogue: 1,")
                    .Append(start).Append(',').Append(stop)
                    .Append(",Caption,,0,0,0,,")
                    .Append(BuildCaptionCrawl(caption, playX, playY, beat.EndSeconds - beat.StartSeconds))
                    .AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(presenter))
        {
            var presenterEnd = presenterEndSeconds ?? (beats.Count == 0 ? 1 : beats[^1].EndSeconds);
            events.Append("Dialogue: 2,")
                .Append(FormatAssTime(presenterStartSeconds)).Append(',')
                .Append(FormatAssTime(presenterEnd))
                .Append(",Presenter,,0,0,0,,")
                .Append(Escape(presenter.Trim()))
                .AppendLine();
        }

        if (events.Length == 0)
        {
            events.Append("Dialogue: 0,0:00:00.00,").Append(end)
                .AppendLine(",Headline,,0,0,0,,{\\b1}Add RSS feeds on the News tab.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("Title: FlowWire News");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("WrapStyle: 2");
        sb.AppendLine("PlayResX: " + playX);
        sb.AppendLine("PlayResY: " + playY);
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine("Style: Headline, Liberation Sans, 32, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 0, 2, 36, 36, 108, 1");
        sb.AppendLine("Style: Caption, Liberation Sans, 40, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 0, 4, 0, 0, 0, 1");
        sb.AppendLine("Style: Presenter, Liberation Sans, 18, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 2, 0, 9, 24, 24, 16, 1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        sb.Append(events);
        return sb.ToString();
    }

    public static string EscapeAssFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string CaptionLine(string body, string title)
    {
        var text = string.IsNullOrWhiteSpace(body) ? title : body;
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildCaptionCrawl(string text, int playX, int playY, double durationSeconds)
    {
        var line = Escape(text);
        const int pxPerChar = 22;
        var y = Math.Max(40, playY - 44);
        var x1 = playX + 48;
        var textWidth = Math.Max(playX / 2, line.Length * pxPerChar);
        var available = Math.Max(durationSeconds, 0.5);
        var moveSeconds = Math.Max(0.4, available - 0.25);
        var x2 = -textWidth;
        var endMs = (int)Math.Round(moveSeconds * 1000);
        var move = string.Format(
            CultureInfo.InvariantCulture,
            "{{\\q2\\move({0},{1},{2},{1},0,{3})}}",
            x1,
            y,
            x2,
            endMs);
        return move + line;
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
