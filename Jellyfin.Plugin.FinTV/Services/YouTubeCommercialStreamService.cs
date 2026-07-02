using System.Text;
using CliWrap;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class YouTubeCommercialStreamService
{
    private static readonly string[] StreamFormats = ["b", "bv*+ba/b", "best[ext=mp4]/best"];

    private readonly ILogger<YouTubeCommercialStreamService> _logger;
    private readonly YtDlpLocator _ytDlpLocator;

    public YouTubeCommercialStreamService(
        ILogger<YouTubeCommercialStreamService> logger,
        YtDlpLocator ytDlpLocator)
    {
        _logger = logger;
        _ytDlpLocator = ytDlpLocator;
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
                    output,
                    cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "yt-dlp pipe stream failed for {Title}; trying direct stream URL", commercial.Title);
            }

            var streamUrl = await ResolveStreamUrlAsync(ytDlp, commercial.YouTubeUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(streamUrl))
            {
                var args = ffmpeg.BuildRemoteMediaCommand(channel, streamUrl, 0, durationSeconds, null);
                await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
                return;
            }
        }

        _logger.LogWarning(
            "yt-dlp is unavailable; attempting direct YouTube URL for {Title} (may fail)",
            commercial.Title);
        var fallbackArgs = ffmpeg.BuildRemoteMediaCommand(channel, commercial.YouTubeUrl, 0, durationSeconds, null);
        await RunFfmpegToStreamAsync(ffmpegPath, fallbackArgs, output, cancellationToken);
    }

    private static async Task StreamViaYtDlpPipeAsync(
        string ytDlpPath,
        string ffmpegPath,
        FfmpegCommandBuilder ffmpeg,
        Channel channel,
        string youtubeUrl,
        double durationSeconds,
        Stream output,
        CancellationToken cancellationToken)
    {
        var ytDlp = Cli.Wrap(ytDlpPath)
            .WithArguments(new[]
            {
                "-f", "b",
                "--no-playlist",
                "--no-part",
                "--no-cache-dir",
                "-o", "-",
                youtubeUrl
            })
            .WithValidation(CommandResultValidation.None);

        var ffmpegArgs = ffmpeg.BuildRemoteMediaCommand(channel, "pipe:0", 0, durationSeconds, null);

        await Cli.Wrap(ffmpegPath)
            .WithArguments(ffmpegArgs)
            .WithStandardInputPipe(PipeSource.FromCommand(ytDlp))
            .WithStandardOutputPipe(PipeTarget.ToStream(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(new StringBuilder()))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

    private async Task<string?> ResolveStreamUrlAsync(
        string ytDlpPath,
        string youtubeUrl,
        CancellationToken cancellationToken)
    {
        foreach (var format in StreamFormats)
        {
            var stdout = new StringBuilder();
            var result = await Cli.Wrap(ytDlpPath)
                .WithArguments(new[]
                {
                    "-g",
                    "-f", format,
                    "--no-playlist",
                    youtubeUrl
                })
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

    private static Task RunFfmpegToStreamAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        Stream output,
        CancellationToken cancellationToken)
    {
        return Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStream(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(new StringBuilder()))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }

}
