using System.Globalization;
using System.Threading.Channels;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.FinTV.Services;

public class WeatherStarChannelService
{
    public const string DefaultWeatherStarBaseUrl = "https://weather.jmthornton.net";

    private const double CaptureFps = 12;
    private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<WeatherStarChannelService> _logger;
    private readonly FfmpegCommandBuilder _ffmpegBuilder;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly PlaywrightRuntimeService _playwrightRuntime;

    public WeatherStarChannelService(
        ILogger<WeatherStarChannelService> logger,
        FfmpegCommandBuilder ffmpegBuilder,
        IMediaEncoder mediaEncoder,
        PlaywrightRuntimeService playwrightRuntime)
    {
        _logger = logger;
        _ffmpegBuilder = ffmpegBuilder;
        _mediaEncoder = mediaEncoder;
        _playwrightRuntime = playwrightRuntime;
    }

    public async Task StreamAsync(Domain.Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var lat = channel.WeatherLatitude ?? 41.60574;
        var lon = channel.WeatherLongitude ?? -93.55002;
        var baseUrl = Plugin.Instance?.Configuration.WeatherStarBaseUrl;
        var weatherPageUrl = BuildWeatherPageUrl(lat, lon, baseUrl);
        var (width, height) = GetResolution(channel);
        var ffmpegPath = _mediaEncoder.EncoderPath;

        try
        {
            using var playwright = await _playwrightRuntime.CreateAsync(cancellationToken);
            await using var browser = await playwright.Chromium.LaunchAsync(_playwrightRuntime.CreateLaunchOptions());
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height }
            });
            var page = await context.NewPageAsync();
            await NavigateToWeatherAsync(page, weatherPageUrl, cancellationToken);

            using var frameStream = new ScreenshotFrameStream();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var ffmpegTask = CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(BuildWeatherFfmpegArgs(width, height))
                .WithStandardInputPipe(CliWrap.PipeSource.FromStream(frameStream))
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
                .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(new System.Text.StringBuilder()))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(linkedCts.Token);

            var captureTask = CaptureWeatherLoopAsync(page, weatherPageUrl, frameStream, linkedCts.Token);

            var completed = await Task.WhenAny(ffmpegTask, captureTask);
            if (completed == captureTask)
            {
                await captureTask;
            }

            linkedCts.Cancel();
            frameStream.Complete();

            try
            {
                await ffmpegTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the viewer disconnects.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WeatherStar capture failed, using offline slate");
            var offline = _ffmpegBuilder.BuildOfflineSlateCommand(channel);
            await CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(offline)
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);
        }
    }

    internal static string BuildWeatherPageUrl(double lat, double lon, string? baseUrl = null)
    {
        var root = NormalizeWeatherStarBaseUrl(baseUrl);
        var separator = root.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{root}{separator}lat={FormatCoordinate(lat)}&lon={FormatCoordinate(lon)}";
    }

    internal static string NormalizeWeatherStarBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultWeatherStarBaseUrl;
        }

        return baseUrl.Trim().TrimEnd('/');
    }

    internal static string FormatCoordinate(double value)
    {
        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static async Task NavigateToWeatherAsync(IPage page, string weatherPageUrl, CancellationToken cancellationToken)
    {
        await page.GotoAsync(weatherPageUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60000
        });
        await page.WaitForTimeoutAsync(5000);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task CaptureWeatherLoopAsync(
        IPage page,
        string weatherPageUrl,
        ScreenshotFrameStream frameStream,
        CancellationToken cancellationToken)
    {
        var frameDelay = TimeSpan.FromSeconds(1.0 / CaptureFps);
        var nextRefresh = DateTime.UtcNow.Add(PageRefreshInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextRefresh)
            {
                await NavigateToWeatherAsync(page, weatherPageUrl, cancellationToken);
                nextRefresh = DateTime.UtcNow.Add(PageRefreshInterval);
            }

            var jpeg = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 85
            });

            await frameStream.WriteFrameAsync(jpeg, cancellationToken);
            await Task.Delay(frameDelay, cancellationToken);
        }
    }

    private static (int Width, int Height) GetResolution(Domain.Channel channel)
    {
        return channel.AspectRatio == AspectRatioMode.FourThree
            ? (640, 480)
            : (854, 480);
    }

    private static IReadOnlyList<string> BuildWeatherFfmpegArgs(int width, int height)
    {
        return new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-f", "image2pipe",
            "-framerate", CaptureFps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-vf", $"scale={width}:{height}",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "stillimage",
            "-pix_fmt", "yuv420p",
            "-an",
            "-f", "mpegts",
            "pipe:1"
        };
    }

    private sealed class ScreenshotFrameStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _frames = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        private byte[]? _currentFrame;
        private int _currentOffset;

        public async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            await _frames.Writer.WriteAsync(frame, cancellationToken);
        }

        public void Complete()
        {
            _frames.Writer.TryComplete();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return 0;
            }

            var totalRead = 0;
            while (totalRead == 0)
            {
                if (_currentFrame is null || _currentOffset >= _currentFrame.Length)
                {
                    if (!await _frames.Reader.WaitToReadAsync(cancellationToken))
                    {
                        return 0;
                    }

                    if (!_frames.Reader.TryRead(out var next))
                    {
                        continue;
                    }

                    _currentFrame = next;
                    _currentOffset = 0;
                }

                var available = _currentFrame.Length - _currentOffset;
                var toCopy = Math.Min(count - totalRead, available);
                Buffer.BlockCopy(_currentFrame, _currentOffset, buffer, offset + totalRead, toCopy);
                _currentOffset += toCopy;
                totalRead += toCopy;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
