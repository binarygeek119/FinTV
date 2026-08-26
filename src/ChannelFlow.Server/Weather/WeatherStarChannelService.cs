using System.Threading.Channels;
using FinTv.Domain;
using FinTv.Streaming;
using FinTv.Weather;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class WeatherStarChannelService
{
    public const string DefaultWeatherStarBaseUrl = "http://127.0.0.1:8080";

    public const string WeatherAlertAttentionFileName = "weather_alert.ogg";

    public const string WeatherAlertEndFileName = "weather_alert_single.mp3";

    public const string DefaultWeatherLocationQuery = "";

    public const string DefaultWeatherStarPermalinkQuery =
        "hazards=true&current-weather=true&latest-observations=true&hourly=true&hourly-graph=true&travel=true&regional-forecast=true&local-forecast=true&extended-forecast=true&almanac=true&spc-outlook=true&radar=true&stickyKiosk=true&customTextEnable=false&speed=1.00&viewMode=standard&units=us&customText=&mediaVolume=0.75&wide=false&portrait=false&enhanced=false";

    public static bool IsUnsetOrLegacyLocation(string? query)
        => WeatherSampleLocations.IsUnsetOrLegacy(query);

    public static string PickRandomLocation(IEnumerable<string>? exclude = null)
        => WeatherSampleLocations.PickRandom(exclude);

    public static string ResolveLocationQuery(string? channelLocation)
    {
        if (!WeatherSampleLocations.IsUnsetOrLegacy(channelLocation))
        {
            return channelLocation!.Trim();
        }

        var fallback = ResolveDefaultLocationQuery();
        return string.IsNullOrWhiteSpace(fallback)
            ? WeatherSampleLocations.PickRandom()
            : fallback;
    }

    public static string ResolveDefaultLocationQuery()
    {
        var configured = FinTvRuntime.Current?.Configuration.WeatherDefaultLocationQuery;
        return WeatherSampleLocations.IsUnsetOrLegacy(configured)
            ? string.Empty
            : configured!.Trim();
    }

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

    private static readonly HashSet<string> DroppedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "scanLines"
    };

    private const double CaptureFps = 10;

    private readonly ILogger<WeatherStarChannelService> _logger;
    private readonly FfmpegCommandBuilder _ffmpegBuilder;
    private readonly EbsService _ebs;
    private readonly IFfmpegLocator _mediaEncoder;
    private readonly JellyfinCatalogService _catalog;
    private readonly WeatherDataClient _weather;
    private readonly WeatherStarCompositor _compositor;
    private readonly WeatherStarAssets _assets;
    private readonly WeatherAlertOverlayService _alerts;

    public WeatherStarChannelService(
        ILogger<WeatherStarChannelService> logger,
        FfmpegCommandBuilder ffmpegBuilder,
        EbsService ebs,
        IFfmpegLocator mediaEncoder,
        JellyfinCatalogService catalog,
        WeatherDataClient weather,
        WeatherStarCompositor compositor,
        WeatherStarAssets assets,
        WeatherAlertOverlayService alerts)
    {
        _logger = logger;
        _ffmpegBuilder = ffmpegBuilder;
        _ebs = ebs;
        _mediaEncoder = mediaEncoder;
        _catalog = catalog;
        _weather = weather;
        _compositor = compositor;
        _assets = assets;
        _alerts = alerts;
    }

    public async Task StreamAsync(Domain.Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var locationQuery = ResolveLocationQuery(channel.WeatherLocationQuery);
        var config = FinTvRuntime.Current?.Configuration;
        var permalinkQuery = config?.WeatherStarPermalinkQuery;
        var source = WeatherDataClient.ParseSource(config?.WeatherSource);
        var useMetric = PermalinkUsesMetric(permalinkQuery);
        var (width, height) = GetResolution(channel);
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var backgroundMusicPath = ResolveWeatherMusicPath();
        var skin = ResolveVariant(channel);
        _logger.LogInformation("WeatherStar stream {Channel} using {Skin}", channel.Name, skin);

        WeatherSnapshot snap;
        try
        {
            snap = await _weather.GetSnapshotAsync(locationQuery, source, useMetric, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Weather data failed for {Channel}; using EBS slate", channel.Name);
            await WriteEbsFallbackAsync(channel, ffmpegPath, output, cancellationToken);
            return;
        }

        var sequencer = new WeatherStarSequencer(
            permalinkQuery,
            skin,
            channel.AspectRatio != AspectRatioMode.FourThree && (config?.WeatherStarAutoWideForSixteenNine ?? true),
            hasAlerts: snap.Alerts.Count > 0,
            localForecastPages: Math.Clamp(snap.Periods.Count > 0 ? snap.Periods.Count : snap.Daily.Count, 1, 6));

        try
        {
            using var frameStream = new ScreenshotFrameStream();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var started = DateTime.UtcNow;
            var ffmpegError = new System.Text.StringBuilder();
            var ffmpegTask = CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(_ffmpegBuilder.BuildWeatherCommand(width, height, CaptureFps, backgroundMusicPath, aspect: channel.AspectRatio))
                .WithStandardInputPipe(CliWrap.PipeSource.FromStream(frameStream))
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output, autoFlush: true))
                .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(ffmpegError))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(linkedCts.Token);

            var pumpTask = PumpFramesAsync(
                channel,
                snap,
                locationQuery,
                source,
                useMetric,
                sequencer,
                width,
                height,
                frameStream,
                started,
                linkedCts.Token);
            var completed = await Task.WhenAny(ffmpegTask, pumpTask);
            if (completed == pumpTask)
            {
                await pumpTask;
            }

            linkedCts.Cancel();
            frameStream.Complete();
            try
            {
                var result = await ffmpegTask;
                if (!cancellationToken.IsCancellationRequested && ffmpegError.Length > 0)
                {
                    _logger.LogWarning(
                        "Weather ffmpeg ended with exit {Exit} for {Channel}: {Error}",
                        result.ExitCode,
                        channel.Name,
                        ffmpegError.ToString().Trim());
                }
            }
            catch (OperationCanceledException)
            {
                // viewer disconnected
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WeatherStar compositor failed, using EBS slate");
            await WriteEbsFallbackAsync(channel, ffmpegPath, output, cancellationToken);
        }
    }

    public async Task StreamHazardsCutInAsync(
        Domain.Channel channel,
        Stream output,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var locationQuery = ResolveLocationQuery(channel.WeatherLocationQuery);
        var config = FinTvRuntime.Current?.Configuration;
        var source = WeatherDataClient.ParseSource(config?.WeatherSource);
        var useMetric = PermalinkUsesMetric(config?.WeatherStarPermalinkQuery);
        WeatherSnapshot snap;
        try
        {
            snap = _alerts.OverlayAlerts(
                await _weather.GetSnapshotAsync(locationQuery, source, useMetric, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var simulated = _alerts.TrySimulationSnapshot();
            if (simulated is null)
            {
                _logger.LogDebug(ex, "Weather alert cut-in skipped; snapshot failed");
                return;
            }

            snap = simulated;
        }

        if (snap.Alerts.Count == 0)
        {
            return;
        }

        var (width, height) = GetCutInResolution(channel);
        var skin = ResolveVariant(channel);
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var backgroundMusicPath = ResolveWeatherMusicPath();
        var middleSeconds = Math.Clamp(duration.TotalSeconds, 5, 120);
        var tones = await CreateToneSandwichAsync(middleSeconds, cancellationToken);
        var durationSeconds = tones.TotalSeconds;

        try
        {
            using var frameStream = new ScreenshotFrameStream();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(durationSeconds + 2));
            var ffmpegError = new System.Text.StringBuilder();
            var ffmpegTask = CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(_ffmpegBuilder.BuildWeatherCommand(
                    width,
                    height,
                    CaptureFps,
                    backgroundMusicPath,
                    durationSeconds,
                    tones.HasTones ? tones : null,
                    channel.AspectRatio))
                .WithStandardInputPipe(CliWrap.PipeSource.FromStream(frameStream))
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output, autoFlush: true))
                .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(ffmpegError))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(linkedCts.Token);

            var pumpTask = PumpHazardsFramesAsync(
                snap,
                skin,
                width,
                height,
                frameStream,
                durationSeconds,
                linkedCts.Token);
            var completed = await Task.WhenAny(ffmpegTask, pumpTask);
            if (completed == pumpTask)
            {
                await pumpTask;
            }

            linkedCts.Cancel();
            frameStream.Complete();
            try
            {
                await ffmpegTask;
            }
            catch (OperationCanceledException)
            {
                // cut-in finished or viewer disconnected
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Weather alert cut-in failed for {Channel}", channel.Name);
        }
    }

    public static WeatherStarDockerVariant ResolveVariant(Domain.Channel channel)
    {
        var tag = FilterDefinition.ExtractFintvLibraryTag(channel.FilterJson) ?? string.Empty;
        var name = channel.Name ?? string.Empty;
        if (IsWeatherStar3000Token(tag) || IsWeatherStar3000Token(name))
        {
            return WeatherStarDockerVariant.Ws3kp;
        }

        return ResolveConfiguredVariant();
    }

    public static WeatherStarDockerVariant ResolveConfiguredVariant()
    {
        var configured = FinTvRuntime.Current?.Configuration.WeatherStarVariant;
        return IsWeatherStar3000Token(configured)
            ? WeatherStarDockerVariant.Ws3kp
            : WeatherStarDockerVariant.Ws4kp;
    }

    private static bool IsWeatherStar3000Token(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && (value.Contains("3000", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ws3", StringComparison.OrdinalIgnoreCase));

    public string? ResolveWeatherMusicPath()
    {
        var config = FinTvRuntime.Current?.Configuration;
        string? fromLibrary = null;
        if (config is not null)
        {
            var selectedId = string.IsNullOrWhiteSpace(config.WeatherMusicLibraryId) ? null : config.WeatherMusicLibraryId;
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                fromLibrary = _catalog.PickPlayableMusicPath(
                    selectedId,
                    config.WeatherMusicLibraryName,
                    fallbackToAllMusic: false);
            }
        }

        var path = PlayableMusicPath(fromLibrary)
            ?? PlayableMusicPath(_ebs.ResolveBackgroundMusicPath())
            ?? _assets.PickRandomMusicPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Weather stream has no music file; encoding silence");
            return null;
        }

        _logger.LogInformation("Weather stream music {File}", Path.GetFileName(path));
        return path;
    }

    public async Task<WeatherAlertToneSandwich> CreateToneSandwichAsync(
        double middleSeconds,
        CancellationToken cancellationToken)
    {
        var middle = Math.Max(1, middleSeconds);
        var attentionPath = ResolveWeatherAlertAudioPath(WeatherAlertAttentionFileName);
        var endPath = ResolveWeatherAlertAudioPath(WeatherAlertEndFileName);
        var attentionSeconds = attentionPath is null
            ? 0
            : await ProbeAudioSecondsAsync(attentionPath, cancellationToken);
        var endSeconds = endPath is null
            ? 0
            : await ProbeAudioSecondsAsync(endPath, cancellationToken);
        if (attentionSeconds > 0.2)
        {
            _logger.LogInformation("Mixing weather alert attention tone {File} over video", Path.GetFileName(attentionPath));
        }

        if (endSeconds > 0.2)
        {
            _logger.LogInformation("Mixing weather alert end chime {File} over video", Path.GetFileName(endPath));
        }

        return new WeatherAlertToneSandwich
        {
            AttentionPath = attentionPath,
            AttentionSeconds = attentionSeconds,
            EndPath = endPath,
            EndSeconds = endSeconds,
            MiddleSeconds = middle
        };
    }

    private async Task<double> ProbeAudioSecondsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var stdout = new System.Text.StringBuilder();
            var encoderDir = Path.GetDirectoryName(_mediaEncoder.EncoderPath);
            var probe = Path.Combine(
                string.IsNullOrWhiteSpace(encoderDir) ? string.Empty : encoderDir,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (!File.Exists(probe))
            {
                probe = "ffprobe";
            }

            await CliWrap.Cli.Wrap(probe)
                .WithArguments([
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path
                ])
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStringBuilder(stdout))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            return double.TryParse(
                stdout.ToString().Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
                ? Math.Clamp(seconds, 0, 45)
                : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not probe weather alert audio {File}", path);
            return 0;
        }
    }

    private static string? PlayableMusicPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    internal static string? ResolveWeatherAlertAudioPath(string fileName)
    {
        var runtime = FinTvRuntime.Current;
        if (runtime is null || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var folder in new[]
                 {
                     Path.Combine(runtime.BundledAudioFolder, EbsService.EbsFolderName),
                     Path.Combine(runtime.LogosFolder, "binarygeek119", "Weather"),
                     Path.Combine(runtime.LogosFolder, "binarygeek119", EbsService.EbsFolderName),
                     Path.Combine(runtime.BundledLogosFolder, "Weather"),
                     runtime.EbsFolder
                 })
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            var path = Path.Combine(folder, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return LogoSetService.ResolveBinarygeek119File(fileName);
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

        foreach (var key in DroppedQueryKeys)
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
        if (WeatherLocationParser.TryParseLatLon(trimmedLocation, out var latitude, out var longitude))
        {
            parameters["lat"] = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["lon"] = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["latLon"] =
                $"{{\"lat\":{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"lon\":{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        }

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

        foreach (var key in DroppedQueryKeys)
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

    private async Task PumpFramesAsync(
        Domain.Channel channel,
        WeatherSnapshot snap,
        string locationQuery,
        WeatherSourceKind source,
        bool useMetric,
        WeatherStarSequencer sequencer,
        int width,
        int height,
        ScreenshotFrameStream frameStream,
        DateTime started,
        CancellationToken cancellationToken)
    {
        var frameDelay = TimeSpan.FromSeconds(1.0 / CaptureFps);
        var current = snap;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - current.FetchedAt > TimeSpan.FromMinutes(8))
            {
                try
                {
                    current = await _weather.GetSnapshotAsync(locationQuery, source, useMetric, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Weather snapshot refresh failed; keeping previous frame data");
                }
            }

            var elapsed = DateTime.UtcNow - started;
            var (screen, radarIndex, screenRepeat) = sequencer.At(elapsed);
            var skin = ResolveVariant(channel);
            var jpeg = _compositor.RenderJpeg(current, screen, skin, width, height, radarIndex, screenRepeat, elapsed);
            await frameStream.WriteFrameAsync(jpeg, cancellationToken);
            await Task.Delay(frameDelay, cancellationToken);
        }
    }

    private async Task PumpHazardsFramesAsync(
        WeatherSnapshot snap,
        WeatherStarDockerVariant skin,
        int width,
        int height,
        ScreenshotFrameStream frameStream,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var frameDelay = TimeSpan.FromSeconds(1.0 / CaptureFps);
        var started = DateTime.UtcNow;
        var frame = 0;
        while (!cancellationToken.IsCancellationRequested
            && (DateTime.UtcNow - started).TotalSeconds < durationSeconds)
        {
            var jpeg = _compositor.RenderJpeg(snap, WeatherStarScreen.Hazards, skin, width, height, frame / 10, elapsed: DateTime.UtcNow - started);
            await frameStream.WriteFrameAsync(jpeg, cancellationToken);
            frame++;
            await Task.Delay(frameDelay, cancellationToken);
        }
    }

    internal static bool PermalinkUsesMetricUnits(string? query)
        => PermalinkUsesMetric(query);

    private static bool PermalinkUsesMetric(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        foreach (var segment in query.Trim().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = segment.IndexOf('=');
            if (sep < 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(segment[..sep]);
            if (key.Equals("units", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segment[(sep + 1)..]).Equals("si", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static (int Width, int Height) GetResolution(Domain.Channel channel)
    {
        return channel.AspectRatio == AspectRatioMode.FourThree
            ? (640, 480)
            : (854, 480);
    }

    private static (int Width, int Height) GetCutInResolution(Domain.Channel channel)
    {
        return channel.AspectRatio == AspectRatioMode.FourThree
            ? (1440, 1080)
            : (1920, 1080);
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
