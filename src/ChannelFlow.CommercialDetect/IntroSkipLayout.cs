namespace ChannelFlow.CommercialDetect;

public static class IntroSkipLayout
{
    private static readonly HashSet<string> IntroSkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "intro", "preview", "recap", "commercial", "outro"
    };

    private static readonly HashSet<string> OpeningNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "intro", "preview", "recap"
    };

    public const double EdgePadSeconds = 3;
    public const double NearStartSeconds = 300;
    public const double NearStartFraction = 0.15;
    public const double PairSlackSeconds = 0.4;

    public static bool IsIntroSkipName(string? name)
        => !string.IsNullOrWhiteSpace(name) && IntroSkipNames.Contains(name.Trim());

    /// <summary>
    /// Jellyfin introskip openers and outro. These sit at the start or end of a file and must not be mid-roll points.
    /// </summary>
    public static bool IsOpeningOrOutroName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        return OpeningNames.Contains(trimmed)
            || trimmed.Equals("outro", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedExistingName(string? name)
        => IsIntroSkipName(name) || BreakChapterNamer.IsBreakName(name);

    public static FileDisposition Classify(IReadOnlyList<FileChapter> chapters)
    {
        if (chapters.Count == 0)
        {
            return FileDisposition.NoChapters;
        }

        return chapters.All(chapter => IsAllowedExistingName(chapter.Name))
            ? FileDisposition.IntroSkipOnly
            : FileDisposition.SkipFile;
    }

    public static IReadOnlyList<TimeRange> EligibleWindows(
        IReadOnlyList<FileChapter> chapters,
        double durationSeconds)
    {
        var duration = Math.Max(durationSeconds, 0);
        if (duration <= EdgePadSeconds * 2)
        {
            return [];
        }

        var disposition = Classify(chapters);
        if (disposition == FileDisposition.SkipFile)
        {
            return [];
        }

        double windowStart = EdgePadSeconds;
        double windowEnd = Math.Max(EdgePadSeconds, duration - EdgePadSeconds);
        var blocked = new List<TimeRange>();

        if (disposition == FileDisposition.IntroSkipOnly)
        {
            var nearStart = Math.Min(NearStartSeconds, duration * NearStartFraction);
            var openingEnd = 0d;
            var outroStart = duration;
            foreach (var chapter in chapters)
            {
                var range = chapter.Range;
                blocked.Add(range);
                if (OpeningNames.Contains(chapter.Name.Trim()) && chapter.StartSeconds <= nearStart)
                {
                    openingEnd = Math.Max(openingEnd, range.EndSeconds);
                }

                if (chapter.Name.Equals("outro", StringComparison.OrdinalIgnoreCase))
                {
                    outroStart = Math.Min(outroStart, chapter.StartSeconds);
                }
            }

            windowStart = Math.Max(windowStart, openingEnd);
            windowEnd = Math.Min(windowEnd, outroStart);
        }

        if (windowEnd <= windowStart + 0.5)
        {
            return [];
        }

        return Subtract(new TimeRange(windowStart, windowEnd), blocked);
    }

    public static bool Contains(IReadOnlyList<TimeRange> windows, double seconds)
        => windows.Any(window => seconds >= window.StartSeconds && seconds <= window.EndSeconds);

    private static List<TimeRange> Subtract(TimeRange span, List<TimeRange> blocked)
    {
        var remaining = new List<TimeRange> { span };
        foreach (var hole in blocked.OrderBy(range => range.StartSeconds))
        {
            var next = new List<TimeRange>();
            foreach (var piece in remaining)
            {
                if (!piece.Overlaps(hole))
                {
                    next.Add(piece);
                    continue;
                }

                if (hole.StartSeconds - piece.StartSeconds >= 0.4)
                {
                    next.Add(new TimeRange(piece.StartSeconds, Math.Min(piece.EndSeconds, hole.StartSeconds)));
                }

                if (piece.EndSeconds - hole.EndSeconds >= 0.4)
                {
                    next.Add(new TimeRange(Math.Max(piece.StartSeconds, hole.EndSeconds), piece.EndSeconds));
                }
            }

            remaining = next;
        }

        return remaining;
    }
}

public enum FileDisposition
{
    NoChapters,
    IntroSkipOnly,
    SkipFile
}
