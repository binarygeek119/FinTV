namespace ChannelFlow.CommercialDetect;

public readonly record struct TimeRange(double StartSeconds, double EndSeconds)
{
    public double Duration => Math.Max(0, EndSeconds - StartSeconds);

    public bool Overlaps(TimeRange other, double slackSeconds = 0)
        => StartSeconds <= other.EndSeconds + slackSeconds
            && other.StartSeconds <= EndSeconds + slackSeconds;

    public double OverlapSeconds(TimeRange other)
    {
        var start = Math.Max(StartSeconds, other.StartSeconds);
        var end = Math.Min(EndSeconds, other.EndSeconds);
        return Math.Max(0, end - start);
    }

    public TimeRange? Intersect(TimeRange other)
    {
        var start = Math.Max(StartSeconds, other.StartSeconds);
        var end = Math.Min(EndSeconds, other.EndSeconds);
        return end > start ? new TimeRange(start, end) : null;
    }
}
