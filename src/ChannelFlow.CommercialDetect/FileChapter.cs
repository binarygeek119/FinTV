namespace ChannelFlow.CommercialDetect;

public sealed class FileChapter
{
    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string Name { get; init; } = string.Empty;

    public TimeRange Range => new(StartSeconds, Math.Max(StartSeconds, EndSeconds));
}
