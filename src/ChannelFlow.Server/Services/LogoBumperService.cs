using System.Globalization;
using System.Text;
using CliWrap;
using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Local logo-repo bumpers inserted into playout (Slappy's Toon Takeover opener).
/// </summary>
public sealed class LogoBumperService
{
    public const string ToonTakeoverRelativePath = "Shows/slappy's_toon_takeover.mp4";
    public const string GuideGroup = "bumper";

    private static readonly TimeSpan FallbackDuration = TimeSpan.FromSeconds(15);

    private readonly LogoSetService _logoSets;
    private readonly IFfmpegLocator _ffmpeg;

    public LogoBumperService(LogoSetService logoSets, IFfmpegLocator ffmpeg)
    {
        _logoSets = logoSets;
        _ffmpeg = ffmpeg;
    }

    public static bool IsHiddenFromGuide(string? guideGroup)
        => string.Equals(guideGroup, "commercial", StringComparison.OrdinalIgnoreCase)
            || string.Equals(guideGroup, GuideGroup, StringComparison.OrdinalIgnoreCase);

    public bool ShouldOpenToonTakeover(Channel channel, int slotIndex)
        => AiPlayoutTemplates.IsToonTakeoverSlot(channel, slotIndex);

    public async Task<TimeSpan?> TryResolveToonTakeoverDurationAsync(CancellationToken cancellationToken)
    {
        var path = await _logoSets.EnsureBinarygeek119FileAsync(ToonTakeoverRelativePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var seconds = await ProbeDurationSecondsAsync(path, cancellationToken);
        if (seconds < 0.5)
        {
            return FallbackDuration;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public static string? ResolveToonTakeoverPath()
        => LogoSetService.ResolveBinarygeek119File(ToonTakeoverRelativePath);

    private async Task<double> ProbeDurationSecondsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var stdout = new StringBuilder();
            await Cli.Wrap(ResolveFfprobe())
                .WithArguments([
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path
                ])
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            return double.TryParse(
                stdout.ToString().Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds)
                ? seconds
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string ResolveFfprobe()
    {
        var dir = Path.GetDirectoryName(_ffmpeg.EncoderPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return "ffprobe";
    }
}
