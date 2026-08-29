namespace ChannelFlow.CommercialDetect;

public sealed class CommercialSpotDetector
{
    public async Task<DetectResult> DetectAsync(
        string videoPath,
        string ffmpegPath,
        string? ffprobePath,
        CommercialBreakScanSettings settings,
        IEnumerable<string?>? extraExistingNames = null,
        CancellationToken cancellationToken = default)
    {
        settings.Clamp();
        var probeBin = string.IsNullOrWhiteSpace(ffprobePath) ? FfmpegTools.ResolveFfprobe(ffmpegPath) : ffprobePath;
        var probe = await FfmpegTools.ProbeAsync(probeBin, videoPath, cancellationToken).ConfigureAwait(false);
        var disposition = IntroSkipLayout.Classify(probe.Chapters);
        var windows = IntroSkipLayout.EligibleWindows(probe.Chapters, probe.DurationSeconds);
        if (disposition == FileDisposition.SkipFile)
        {
            return new DetectResult(disposition, probe, windows, [], []);
        }

        var (black, silence) = await FfmpegTools.DetectAsync(
            ffmpegPath,
            videoPath,
            settings,
            probe.FramesPerSecond,
            cancellationToken).ConfigureAwait(false);

        var candidates = Score(black, silence, windows, settings.ConfidencePercent);
        var accepted = candidates
            .Where(candidate => candidate.Accepted)
            .OrderBy(candidate => candidate.AtSeconds)
            .ToList();
        IEnumerable<string?> existingNames = probe.Chapters.Select(chapter => (string?)chapter.Name);
        if (extraExistingNames is not null)
        {
            existingNames = existingNames.Concat(extraExistingNames);
        }

        var names = BreakChapterNamer.NextNames(accepted.Count, existingNames);
        for (var i = 0; i < accepted.Count; i++)
        {
            accepted[i].Name = names[i];
        }

        return new DetectResult(disposition, probe, windows, candidates, accepted);
    }

    private static List<SpotCandidate> Score(
        IReadOnlyList<TimeRange> black,
        IReadOnlyList<TimeRange> silence,
        IReadOnlyList<TimeRange> windows,
        int threshold)
    {
        var usedSilence = new HashSet<int>();
        var candidates = new List<SpotCandidate>();
        foreach (var dark in black.OrderBy(range => range.StartSeconds))
        {
            var pairIndex = -1;
            TimeRange? pair = null;
            var bestOverlap = -1d;
            for (var i = 0; i < silence.Count; i++)
            {
                if (usedSilence.Contains(i))
                {
                    continue;
                }

                var quiet = silence[i];
                if (!dark.Overlaps(quiet, IntroSkipLayout.PairSlackSeconds))
                {
                    continue;
                }

                var overlap = dark.OverlapSeconds(quiet);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    pair = quiet;
                    pairIndex = i;
                }
            }

            if (pairIndex >= 0)
            {
                usedSilence.Add(pairIndex);
            }

            candidates.Add(MakeCandidate(dark, pair, bestOverlap, windows, threshold));
        }

        for (var i = 0; i < silence.Count; i++)
        {
            if (usedSilence.Contains(i))
            {
                continue;
            }

            candidates.Add(MakeCandidate(null, silence[i], 0, windows, threshold));
        }

        return candidates;
    }

    private static SpotCandidate MakeCandidate(
        TimeRange? black,
        TimeRange? silence,
        double overlapSeconds,
        IReadOnlyList<TimeRange> windows,
        int threshold)
    {
        var blackPresent = black is not null;
        var silencePresent = silence is not null;
        var maxDur = Math.Max(black?.Duration ?? 0, silence?.Duration ?? 0);
        var overlapFrac = blackPresent && silencePresent && maxDur > 0 ? overlapSeconds / maxDur : 0;
        var confidence = 100 * ((blackPresent ? 0.45 : 0) + (silencePresent ? 0.45 : 0) + (0.10 * overlapFrac));
        var at = black is TimeRange dark && silence is TimeRange quiet
            ? (quiet.Intersect(dark)?.StartSeconds ?? Math.Min(dark.StartSeconds, quiet.StartSeconds))
            : (black?.StartSeconds ?? silence?.StartSeconds ?? 0);
        var inWindow = IntroSkipLayout.Contains(windows, at);
        return new SpotCandidate
        {
            Black = black,
            Silence = silence,
            AtSeconds = at,
            Confidence = Math.Clamp(confidence, 0, 100),
            InEligibleWindow = inWindow,
            Accepted = inWindow && confidence + 0.0001 >= threshold
        };
    }
}

public sealed class DetectResult
{
    public DetectResult(
        FileDisposition disposition,
        MediaProbe probe,
        IReadOnlyList<TimeRange> eligibleWindows,
        IReadOnlyList<SpotCandidate> candidates,
        IReadOnlyList<SpotCandidate> accepted)
    {
        Disposition = disposition;
        Probe = probe;
        EligibleWindows = eligibleWindows;
        Candidates = candidates;
        Accepted = accepted;
    }

    public FileDisposition Disposition { get; }

    public MediaProbe Probe { get; }

    public IReadOnlyList<TimeRange> EligibleWindows { get; }

    public IReadOnlyList<SpotCandidate> Candidates { get; }

    public IReadOnlyList<SpotCandidate> Accepted { get; }
}

public sealed class SpotCandidate
{
    public TimeRange? Black { get; init; }

    public TimeRange? Silence { get; init; }

    public double AtSeconds { get; init; }

    public double Confidence { get; init; }

    public bool InEligibleWindow { get; init; }

    public bool Accepted { get; init; }

    public string Name { get; set; } = "break";
}
