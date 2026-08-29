namespace ChannelFlow.CommercialDetect;

/// <summary>
/// Shared blackdetect / silencedetect thresholds used by ChannelFlow and Commercial Spot Tester.
/// </summary>
public sealed class CommercialBreakScanSettings
{
    public bool ScanEnabled { get; set; }

    public bool WriteChaptersToFiles { get; set; }

    public double SilenceDb { get; set; } = -40;

    public double SilenceMinSeconds { get; set; } = 0.3;

    public double BlackPixThreshold { get; set; } = 0.10;

    public double BlackPictureRatio { get; set; } = 0.95;

    public int BlackMinFrames { get; set; } = 6;

    public int ConfidencePercent { get; set; } = 70;

    public void Clamp()
    {
        SilenceDb = Math.Clamp(SilenceDb, -90, 0);
        SilenceMinSeconds = Math.Clamp(SilenceMinSeconds, 0.05, 5);
        BlackPixThreshold = Math.Clamp(BlackPixThreshold, 0.01, 0.5);
        BlackPictureRatio = Math.Clamp(BlackPictureRatio, 0.5, 1);
        BlackMinFrames = Math.Clamp(BlackMinFrames, 1, 120);
        ConfidencePercent = Math.Clamp(ConfidencePercent, 1, 100);
    }

    public double BlackMinSeconds(double framesPerSecond)
    {
        var fps = framesPerSecond > 1 && framesPerSecond < 240 ? framesPerSecond : 30;
        return Math.Max(1, BlackMinFrames) / fps;
    }
}
