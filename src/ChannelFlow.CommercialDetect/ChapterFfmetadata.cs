using System.Globalization;
using System.Text;

namespace ChannelFlow.CommercialDetect;

public static class ChapterFfmetadata
{
    public static string Build(IReadOnlyList<FileChapter> chapters)
    {
        var ordered = chapters
            .OrderBy(chapter => chapter.StartSeconds)
            .ToList();
        var builder = new StringBuilder();
        builder.AppendLine(";FFMETADATA1");
        for (var i = 0; i < ordered.Count; i++)
        {
            var chapter = ordered[i];
            var startMs = ToMs(chapter.StartSeconds);
            var endSeconds = chapter.EndSeconds > chapter.StartSeconds
                ? chapter.EndSeconds
                : i + 1 < ordered.Count
                    ? ordered[i + 1].StartSeconds
                    : chapter.StartSeconds + 1;
            var endMs = Math.Max(startMs + 1, ToMs(endSeconds));
            builder.AppendLine("[CHAPTER]");
            builder.AppendLine("TIMEBASE=1/1000");
            builder.AppendLine("START=" + startMs.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("END=" + endMs.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("title=" + Escape(chapter.Name));
        }

        return builder.ToString();
    }

    private static long ToMs(double seconds)
        => (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);

    private static string Escape(string name)
        => name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("=", "\\=", StringComparison.Ordinal);
}
