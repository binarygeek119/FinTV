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

    public const string DefaultWeatherLocationQuery = "50317, Des Moines, IA, USA";

    public const string DefaultWeatherStarPermalinkQuery =
        "hazards=true&current-weather=true&latest-observations=true&hourly=true&hourly-graph=true&travel=true&regional-forecast=true&local-forecast=true&extended-forecast=true&almanac=true&spc-outlook=true&radar=true&stickyKiosk=true&customTextEnable=false&speed=1.00&viewMode=standard&units=us&customText=&mediaVolume=0.75&wide=false&portrait=false&enhanced=false&scanLines=false";

    private static readonly HashSet<string> LocationQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "latLonQuery",
        "latLon",
        "txtLocation",
        "lat",
        "lon"
    };

    private static readonly HashSet<string> CaptureTimeQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kiosk",
        "wide"
    };

    private const double CaptureFps = 12;
    private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<WeatherStarChannelService> _logger;
    private readonly FfmpegCommandBuilder _ffmpegBuilder;
    private readonly EbsService _ebs;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly PlaywrightRuntimeService _playwrightRuntime;
    private readonly WeatherStarDockerService _weatherDocker;

    public WeatherStarChannelService(
        ILogger<WeatherStarChannelService> logger,
        FfmpegCommandBuilder ffmpegBuilder,
        EbsService ebs,
        IMediaEncoder mediaEncoder,
        PlaywrightRuntimeService playwrightRuntime,
        WeatherStarDockerService weatherDocker)
    {
        _logger = logger;
        _ffmpegBuilder = ffmpegBuilder;
        _ebs = ebs;
        _mediaEncoder = mediaEncoder;
        _playwrightRuntime = playwrightRuntime;
        _weatherDocker = weatherDocker;
    }

    public async Task StreamAsync(Domain.Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var baseUrl = Plugin.Instance?.Configuration.WeatherStarBaseUrl;
        var localVariant = _weatherDocker.ResolveLocalVariant(baseUrl);
        if (localVariant.HasValue)
        {
            await _weatherDocker.EnsureRunningAsync(localVariant.Value, cancellationToken);
        }

        var locationQuery = string.IsNullOrWhiteSpace(channel.WeatherLocationQuery)
            ? DefaultWeatherLocationQuery
            : channel.WeatherLocationQuery.Trim();
        var permalinkQuery = Plugin.Instance?.Configuration.WeatherStarPermalinkQuery;
        var autoWideForSixteenNine = Plugin.Instance?.Configuration.WeatherStarAutoWideForSixteenNine ?? true;
        var weatherPageUrl = _playwrightRuntime.AdjustWeatherPageUrlForRuntime(
            BuildWeatherPageUrl(
                locationQuery,
                baseUrl,
                permalinkQuery,
                autoWideForSixteenNine,
                channel.AspectRatio));
        var (width, height) = GetResolution(channel);
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var backgroundMusicPath = _ebs.ResolveBackgroundMusicPath();

        IBrowser? browser = null;
        var sharedDockerCdp = false;
        try
        {
            using var playwright = await _playwrightRuntime.CreateAsync(cancellationToken);
            (browser, sharedDockerCdp) = await _playwrightRuntime.ConnectBrowserAsync(playwright, cancellationToken);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = width, Height = height }
            });
            var page = await context.NewPageAsync();
            await NavigateToWeatherAsync(page, weatherPageUrl, cancellationToken);

            using var frameStream = new ScreenshotFrameStream();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var ffmpegTask = CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(_ffmpegBuilder.BuildWeatherCommand(width, height, CaptureFps, backgroundMusicPath))
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
            _logger.LogWarning(ex, "WeatherStar capture failed, using EBS slate");
            await WriteEbsFallbackAsync(channel, ffmpegPath, output, cancellationToken);
        }
        finally
        {
            if (browser is not null)
            {
                await _playwrightRuntime.ReleaseBrowserAsync(browser, sharedDockerCdp);
            }
        }
    }

    internal static string BuildWeatherPageUrl(
        string locationQuery,
        string? baseUrl = null,
        string? permalinkQuery = null,
        bool autoWideForSixteenNine = false,
        AspectRatioMode aspectRatio = AspectRatioMode.SixteenNine)
    {
        var root = NormalizeWeatherStarBaseUrl(baseUrl);
        var parameters = ParseQueryParameters(permalinkQuery ?? DefaultWeatherStarPermalinkQuery);

        foreach (var key in LocationQueryKeys)
        {
            parameters.Remove(key);
        }

        parameters["kiosk"] = "true";
        if (autoWideForSixteenNine)
        {
            parameters["wide"] = aspectRatio == AspectRatioMode.FourThree ? "false" : "true";
        }

        var trimmedLocation = locationQuery.Trim();
        parameters["latLonQuery"] = trimmedLocation;
        parameters["txtLocation"] = trimmedLocation;

        return $"{root}?{FormatQueryParameters(parameters)}";
    }

    internal static (string BaseUrl, string Query) SplitPermalink(string permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink))
        {
            return (DefaultWeatherStarBaseUrl, DefaultWeatherStarPermalinkQuery);
        }

        var trimmed = permalink.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return (DefaultWeatherStarBaseUrl, NormalizePermalinkQuery(trimmed));
        }

        var query = NormalizePermalinkQuery(uri.Query);
        var baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return (string.IsNullOrWhiteSpace(baseUrl) ? DefaultWeatherStarBaseUrl : baseUrl, query);
    }

    internal static string NormalizePermalinkQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DefaultWeatherStarPermalinkQuery;
        }

        var trimmed = query.Trim();
        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        var parameters = ParseQueryParameters(trimmed);
        foreach (var key in LocationQueryKeys)
        {
            parameters.Remove(key);
        }

        foreach (var key in CaptureTimeQueryKeys)
        {
            parameters.Remove(key);
        }

        return parameters.Count == 0
            ? DefaultWeatherStarPermalinkQuery
            : FormatQueryParameters(parameters);
    }

    internal static string NormalizeWeatherStarBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultWeatherStarBaseUrl;
        }

        var trimmed = baseUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0 ? trimmed.TrimEnd('/') : trimmed[..queryIndex].TrimEnd('/');
    }

    private static Dictionary<string, string> ParseQueryParameters(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.Trim();
        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(segment)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(segment[..separatorIndex]);
            var value = Uri.UnescapeDataString(segment[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string FormatQueryParameters(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        return string.Join(
            "&",
            parameters.Select(pair =>
                string.IsNullOrEmpty(pair.Value)
                    ? Uri.EscapeDataString(pair.Key)
                    : $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
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

    private async Task WriteEbsFallbackAsync(
        Domain.Channel channel,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        var plan = _ebs.CreatePlaybackPlan(channel, durationSeconds: 120);
        var args = _ffmpegBuilder.BuildEbsCommand(channel, plan);
        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
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
