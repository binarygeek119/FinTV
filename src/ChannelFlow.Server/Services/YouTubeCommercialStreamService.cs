using System.Text;
using CliWrap;
using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Streaming;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class YouTubeCommercialStreamService
{
    private const string TestVideoUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private static readonly string[] StreamFormats = ["b", "bv*+ba/b", "best[ext=mp4]/best"];
    private static readonly string[] PremiumFormats = ["bv*[height<=1080]+ba/b", "b", "best"];

    private readonly ILogger<YouTubeCommercialStreamService> _logger;
    private readonly YtDlpLocator _ytDlpLocator;
    private readonly YouTubeCookieStore _cookies;
    private readonly SponsorBlockClient _sponsorBlock;

    public YouTubeCommercialStreamService(
        ILogger<YouTubeCommercialStreamService> logger,
        YtDlpLocator ytDlpLocator,
        YouTubeCookieStore cookies,
        SponsorBlockClient sponsorBlock)
    {
        _logger = logger;
        _ytDlpLocator = ytDlpLocator;
        _cookies = cookies;
        _sponsorBlock = sponsorBlock;
    }

    public async Task StreamCommercialAsync(
        Channel channel,
        Commercial commercial,
        FfmpegCommandBuilder ffmpeg,
        string ffmpegPath,
        double durationSeconds,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (commercial.Source != CommercialSource.CommercialBrainz)
        {
            throw new InvalidOperationException("Only CommercialBrainz commercials can be streamed from YouTube.");
        }

        if (string.IsNullOrWhiteSpace(commercial.YouTubeUrl))
        {
            throw new InvalidOperationException($"Commercial {commercial.Title} has no YouTube URL.");
        }

        var settings = FinTvRuntime.Current?.Configuration.YouTube ?? new YouTubeSettings();
        var skipRanges = await ResolveSkipRangesAsync(commercial, settings, durationSeconds, cancellationToken);
        var ytDlp = _ytDlpLocator.Resolve();
        if (ytDlp is not null)
        {
            try
            {
                await StreamViaYtDlpPipeAsync(
                    ytDlp,
                    ffmpegPath,
                    ffmpeg,
                    channel,
                    commercial.YouTubeUrl,
                    durationSeconds,
                    skipRanges,
                    settings,
                    output,
                    cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "yt-dlp pipe stream failed for {Title}; trying direct stream URL", commercial.Title);
            }

            var streamUrl = await ResolveStreamUrlAsync(ytDlp, commercial.YouTubeUrl, settings, cancellationToken);
            if (!string.IsNullOrWhiteSpace(streamUrl))
            {
                var args = ffmpeg.BuildRemoteMediaCommand(channel, streamUrl, 0, durationSeconds, null, skipRanges, overlayBug: false);
                await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
                return;
            }
        }

        _logger.LogWarning(
            "yt-dlp is unavailable; attempting direct YouTube URL for {Title} (may fail)",
            commercial.Title);
        var fallbackArgs = ffmpeg.BuildRemoteMediaCommand(channel, commercial.YouTubeUrl, 0, durationSeconds, null, skipRanges, overlayBug: false);
        await RunFfmpegToStreamAsync(ffmpegPath, fallbackArgs, output, cancellationToken);
    }

    public async Task<object> TestAccountAsync(CancellationToken cancellationToken)
    {
        var ytDlp = _ytDlpLocator.Resolve();
        if (ytDlp is null)
        {
            return new
            {
                ok = false,
                message = "yt-dlp was not found. Install yt-dlp on the server or set CHANNELFLOW_YTDLP_PATH."
            };
        }

        var settings = FinTvRuntime.Current?.Configuration.YouTube ?? new YouTubeSettings();
        var cookieStatus = _cookies.GetStatus();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var args = BuildYtDlpArgs(settings, ["--skip-download", "--print", "%(id)s", "--print", "%(title)s", TestVideoUrl]);
        var result = await Cli.Wrap(ytDlp)
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            return new
            {
                ok = false,
                hasCookies = cookieStatus.HasCookies,
                looksSignedIn = cookieStatus.LooksSignedIn,
                message = "yt-dlp could not resolve a YouTube video. Update yt-dlp or paste a fresh cookies.txt from a signed-in browser."
            };
        }

        var lines = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var title = lines.Length > 1 ? lines[1] : lines.FirstOrDefault();
        var signedIn = cookieStatus.LooksSignedIn;
        var premiumHint = settings.PreferPremium && signedIn
            ? " Cookies look signed in; a YouTube Premium account can unlock higher-quality formats."
            : signedIn
                ? " Cookies look signed in."
                : cookieStatus.HasCookies
                    ? " Cookies are saved but may not include a YouTube login (SID / SAPISID)."
                    : " No cookies saved — public videos may still play, but Premium and many age-gated videos will not.";

        return new
        {
            ok = true,
            hasCookies = cookieStatus.HasCookies,
            looksSignedIn = signedIn,
            title,
            message = "YouTube playback is ready." + premiumHint
        };
    }

    private async Task<IReadOnlyList<SponsorSkipRange>> ResolveSkipRangesAsync(
        Commercial commercial,
        YouTubeSettings settings,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!settings.SponsorBlockEnabled)
        {
            return [];
        }

        var videoId = commercial.YouTubeVideoId;
        if (!YouTubeUrlHelper.TryGetVideoId(videoId, out var id))
        {
            YouTubeUrlHelper.TryGetVideoId(commercial.YouTubeUrl, out id);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return [];
        }

        var ranges = await _sponsorBlock.GetSkipRangesAsync(id, settings, cancellationToken);
        var playback = FfmpegSkipCuts.ForPlayback(ranges, durationSeconds);
        if (playback.Count > 0)
        {
            _logger.LogInformation(
                "SponsorBlock will skip {Count} segment(s) in {Title}",
                playback.Count,
                commercial.Title);
        }

        return playback;
    }

    private async Task StreamViaYtDlpPipeAsync(
        string ytDlpPath,
        string ffmpegPath,
        FfmpegCommandBuilder ffmpeg,
        Channel channel,
        string youtubeUrl,
        double durationSeconds,
        IReadOnlyList<SponsorSkipRange> skipRanges,
        YouTubeSettings settings,
        Stream output,
        CancellationToken cancellationToken)
    {
        var ytDlpError = new StringBuilder();
        var ytDlp = Cli.Wrap(ytDlpPath)
            .WithArguments(BuildYtDlpArgs(settings, [
                "-f", GetPipeFormat(settings),
                "--merge-output-format", "mkv",
                "--remux-video", "mkv",
                "-o", "-",
                youtubeUrl
            ]))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(ytDlpError))
            .WithValidation(CommandResultValidation.None);

        var ffmpegArgs = ffmpeg.BuildRemoteMediaCommand(channel, "pipe:0", 0, durationSeconds, null, skipRanges, overlayBug: false);
        var ffmpegError = new StringBuilder();
        var result = await Cli.Wrap(ffmpegPath)
            .WithArguments(ffmpegArgs)
            .WithStandardInputPipe(PipeSource.FromCommand(ytDlp))
            .WithStandardOutputPipe(PipeTarget.ToStream(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(ffmpegError))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "YouTube pipe ffmpeg exited "
                + result.ExitCode
                + ": "
                + TrimProcessOutput(ffmpegError, ytDlpError));
        }
    }

    private string GetPipeFormat(YouTubeSettings settings)
        => settings.PreferPremium && _cookies.HasCookies()
            ? "bv*[height<=1080]+ba/b"
            : "bv*+ba/b";

    private async Task<string?> ResolveStreamUrlAsync(
        string ytDlpPath,
        string youtubeUrl,
        YouTubeSettings settings,
        CancellationToken cancellationToken)
    {
        var formats = settings.PreferPremium && _cookies.HasCookies()
            ? PremiumFormats
            : StreamFormats;

        foreach (var format in formats)
        {
            var stdout = new StringBuilder();
            var result = await Cli.Wrap(ytDlpPath)
                .WithArguments(BuildYtDlpArgs(settings, ["-g", "-f", format, youtubeUrl]))
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                continue;
            }

            var lines = stdout.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 1)
            {
                return lines[0];
            }

            if (lines.Length >= 2)
            {
                _logger.LogDebug(
                    "yt-dlp returned separate audio/video URLs for {Url}; prefer pipe streaming",
                    youtubeUrl);
            }
        }

        return null;
    }

    private List<string> BuildYtDlpArgs(YouTubeSettings settings, IReadOnlyList<string> tail)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--no-part",
            "--no-cache-dir",
            "--no-warnings",
            "--no-progress"
        };

        var cookiePath = _cookies.GetPathIfPresent();
        if (!string.IsNullOrWhiteSpace(cookiePath))
        {
            args.Add("--cookies");
            args.Add(cookiePath);
        }

        if (settings.PreferPremium || cookiePath is not null)
        {
            args.Add("--extractor-args");
            args.Add("youtube:player_client=tv,android,web");
        }

        args.AddRange(tail);
        return args;
    }

    private static async Task RunFfmpegToStreamAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        Stream output,
        CancellationToken cancellationToken)
    {
        var stderr = new StringBuilder();
        var result = await Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStream(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "ffmpeg exited " + result.ExitCode + ": " + TrimProcessOutput(stderr));
        }
    }

    private static string TrimProcessOutput(params StringBuilder[] outputs)
    {
        var text = string.Join(
            " | ",
            outputs
                .Select(output => output.ToString().Trim())
                .Where(line => line.Length > 0));
        if (text.Length <= 1500)
        {
            return text;
        }

        return text[^1500..];
    }
}
