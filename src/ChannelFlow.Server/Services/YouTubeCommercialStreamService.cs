using System.Collections.Concurrent;
using System.Diagnostics;
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

    // tv / tv_downgraded currently return "The page needs to be reloaded".
    private static readonly string[] PlayerClientAttempts =
    [
        "youtube:player_client=default,web_embedded,-tv_downgraded,-tv",
        "youtube:player_client=web_safari,web_embedded,-tv_downgraded,-tv",
        "youtube:player_client=android_vr,web_embedded,-tv_downgraded,-tv"
    ];

    private static readonly string[] StreamFormats = ["bv*+ba/b", "b"];
    private static readonly string[] PremiumFormats = ["bv*[height<=1080]+ba/b", "bv*+ba/b", "b"];

    private readonly ILogger<YouTubeCommercialStreamService> _logger;
    private readonly YtDlpLocator _ytDlpLocator;
    private readonly YouTubeCookieStore _cookies;
    private readonly SponsorBlockClient _sponsorBlock;
    private readonly ConcurrentDictionary<Guid, Task<PrefetchedCommercial?>> _prefetches = new();
    private int _loggedUnusableCookies;

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
        var ytDlp = _ytDlpLocator.Resolve();
        var prefetched = await TakePrefetchAsync(commercial.Id);
        var skipRanges = prefetched?.SkipRanges ?? [];

        if (ytDlp is not null && !string.IsNullOrWhiteSpace(prefetched?.StreamUrl))
        {
            try
            {
                var args = ffmpeg.BuildRemoteMediaCommand(
                    channel,
                    prefetched.StreamUrl,
                    0,
                    durationSeconds,
                    null,
                    skipRanges,
                    overlayBug: false);
                await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Prefetched YouTube URL failed for {Title}; trying live pipe", commercial.Title);
            }
        }

        if (ytDlp is null)
        {
            throw new InvalidOperationException(
                "yt-dlp was not found. Install yt-dlp or set CHANNELFLOW_YTDLP_PATH; ffmpeg cannot open a YouTube watch page.");
        }

        Exception? pipeError = null;
        foreach (var clientArgs in PlayerClientAttempts)
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
                    clientArgs,
                    output,
                    cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pipeError = ex;
                if (!ShouldRetryYouTubeExtract(ex))
                {
                    _logger.LogWarning(ex, "yt-dlp pipe stream failed for {Title}; trying a direct stream URL", commercial.Title);
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "yt-dlp pipe failed for {Title} with {Clients}; trying another YouTube client",
                    commercial.Title,
                    clientArgs);
            }
        }

        var streamUrl = await ResolveStreamUrlAsync(ytDlp, commercial.YouTubeUrl, settings, cancellationToken);
        if (!string.IsNullOrWhiteSpace(streamUrl))
        {
            var args = ffmpeg.BuildRemoteMediaCommand(channel, streamUrl, 0, durationSeconds, null, skipRanges, overlayBug: false);
            await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
            return;
        }

        throw pipeError ?? new InvalidOperationException(
            "yt-dlp could not resolve a YouTube stream. Update yt-dlp or paste a fresh cookies.txt from a signed-in browser.");
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
        var args = BuildYtDlpArgs(
            settings,
            PlayerClientAttempts[0],
            ["--skip-download", "--print", "%(id)s", "--print", "%(title)s", TestVideoUrl]);
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
        string playerClients,
        Stream output,
        CancellationToken cancellationToken)
    {
        var ytDlpError = new StringBuilder();
        var ytDlp = Cli.Wrap(ytDlpPath)
            .WithArguments(BuildYtDlpArgs(settings, playerClients, [
                "-f", GetPipeFormat(settings),
                "--merge-output-format", "mkv",
                "--remux-video", "mkv",
                "--hls-use-mpegts",
                "-o", "-",
                youtubeUrl
            ]))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(ytDlpError))
            .WithValidation(CommandResultValidation.None);

        var ffmpegArgs = ffmpeg.BuildRemoteMediaCommand(channel, "pipe:0", 0, durationSeconds, null, skipRanges, overlayBug: false);
        var ffmpegError = new StringBuilder();
        var started = Stopwatch.StartNew();
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

        if (!cancellationToken.IsCancellationRequested
            && durationSeconds >= 6
            && started.Elapsed.TotalSeconds < 2.5)
        {
            throw new InvalidOperationException(
                "YouTube pipe ended after "
                + started.Elapsed.TotalSeconds.ToString("0.0")
                + "s (wanted "
                + durationSeconds.ToString("0")
                + "s): "
                + TrimProcessOutput(ffmpegError, ytDlpError));
        }
    }

    /// <summary>
    /// Resolves the YouTube stream URL in the background so the next spot can start without waiting on yt-dlp.
    /// </summary>
    public void BeginPrefetch(Commercial commercial, double durationSeconds, CancellationToken cancellationToken)
    {
        if (commercial.Source != CommercialSource.CommercialBrainz
            || string.IsNullOrWhiteSpace(commercial.YouTubeUrl)
            || _ytDlpLocator.Resolve() is null)
        {
            return;
        }

        _prefetches.GetOrAdd(
            commercial.Id,
            _ => PrefetchAsync(commercial, durationSeconds, cancellationToken));
    }

    private async Task<PrefetchedCommercial?> TakePrefetchAsync(Guid commercialId)
    {
        if (!_prefetches.TryGetValue(commercialId, out var task) || !task.IsCompleted)
        {
            return null;
        }

        _prefetches.TryRemove(commercialId, out _);
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "YouTube commercial prefetch was not ready");
            return null;
        }
    }

    private async Task<PrefetchedCommercial?> PrefetchAsync(
        Commercial commercial,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var settings = FinTvRuntime.Current?.Configuration.YouTube ?? new YouTubeSettings();
        var skipTask = ResolveSkipRangesAsync(commercial, settings, durationSeconds, cancellationToken);
        var ytDlp = _ytDlpLocator.Resolve();
        Task<string?> urlTask = ytDlp is null
            ? Task.FromResult<string?>(null)
            : ResolveStreamUrlAsync(ytDlp, commercial.YouTubeUrl!, settings, cancellationToken);

        await Task.WhenAll(skipTask, urlTask);
        var url = await urlTask;
        var skipRanges = await skipTask;
        if (string.IsNullOrWhiteSpace(url))
        {
            return skipRanges.Count == 0 ? null : new PrefetchedCommercial(null, skipRanges);
        }

        _logger.LogDebug("Prefetched YouTube stream URL for {Title}", commercial.Title);
        return new PrefetchedCommercial(url, skipRanges);
    }

    private sealed record PrefetchedCommercial(string? StreamUrl, IReadOnlyList<SponsorSkipRange> SkipRanges);

    private string GetPipeFormat(YouTubeSettings settings)
        => settings.PreferPremium && _cookies.GetPathIfUsable() is not null
            ? "bv*[height<=1080]+ba/b"
            : "bv*+ba/b";

    private async Task<string?> ResolveStreamUrlAsync(
        string ytDlpPath,
        string youtubeUrl,
        YouTubeSettings settings,
        CancellationToken cancellationToken)
    {
        var formats = settings.PreferPremium && _cookies.GetPathIfUsable() is not null
            ? PremiumFormats
            : StreamFormats;

        foreach (var format in formats)
        {
            foreach (var clientArgs in PlayerClientAttempts)
            {
                var stdout = new StringBuilder();
                var result = await Cli.Wrap(ytDlpPath)
                    .WithArguments(BuildYtDlpArgs(settings, clientArgs, ["-g", "-f", format, youtubeUrl]))
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);

                if (result.ExitCode != 0)
                {
                    continue;
                }

                var lines = stdout.ToString()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length == 1 && !lines[0].Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase))
                {
                    return lines[0];
                }

                if (lines.Length >= 2)
                {
                    _logger.LogDebug(
                        "yt-dlp returned separate audio/video URLs for {Url}; prefer pipe streaming",
                        youtubeUrl);
                    break;
                }
            }
        }

        return null;
    }

    private List<string> BuildYtDlpArgs(YouTubeSettings settings, string playerClients, IReadOnlyList<string> tail)
    {
        _ = settings;
        var args = new List<string>
        {
            "--no-playlist",
            "--no-part",
            "--no-cache-dir",
            "--no-warnings",
            "--no-progress",
            "--extractor-retries", "3"
        };

        var cookiePath = _cookies.GetPathIfUsable();
        if (!string.IsNullOrWhiteSpace(cookiePath))
        {
            args.Add("--cookies");
            args.Add(cookiePath);
        }
        else if (_cookies.HasCookies() && Interlocked.Exchange(ref _loggedUnusableCookies, 1) == 0)
        {
            _logger.LogWarning(
                "Saved YouTube cookies are not a usable Netscape cookies.txt; commercials will play without cookies until you paste a fresh export");
        }

        args.Add("--extractor-args");
        args.Add(playerClients);

        args.AddRange(tail);
        return args;
    }

    private static bool ShouldRetryYouTubeExtract(Exception ex)
    {
        var text = ex.Message;
        return text.Contains("page needs to be reloaded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("UNPLAYABLE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exited 139", StringComparison.OrdinalIgnoreCase);
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
