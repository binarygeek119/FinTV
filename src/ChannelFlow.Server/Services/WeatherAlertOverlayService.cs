using System.Text;
using CliWrap;
using FinTv.Domain;
using FinTv.Weather;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public sealed class WeatherAlertOverlayService
{
    private const int PrerenderWidth = 1920;
    private const int PrerenderHeight = 1080;
    private const double ShowDuckVolume = 0.2;

    private readonly WeatherDataClient _weather;
    private readonly WeatherStarCompositor _compositor;
    private readonly WeatherStarAssets _assets;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ILogger<WeatherAlertOverlayService> _logger;
    private readonly object _gate = new();
    private AlertOverlaySimulation? _simulation;
    private string? _prerenderKey;
    private Task<string?>? _prerenderTask;

    public const double DuckedShowVolume = ShowDuckVolume;

    public WeatherAlertOverlayService(
        WeatherDataClient weather,
        WeatherStarCompositor compositor,
        WeatherStarAssets assets,
        IFfmpegLocator ffmpeg,
        ILogger<WeatherAlertOverlayService> logger)
    {
        _weather = weather;
        _compositor = compositor;
        _assets = assets;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public static WeatherAlertOverlayMode ParseMode(string? value)
    {
        if (string.Equals(value, "cutin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "cut-in", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "screen", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherAlertOverlayMode.CutIn;
        }

        if (string.Equals(value, "ticker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "scroll", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherAlertOverlayMode.Ticker;
        }

        return WeatherAlertOverlayMode.Off;
    }

    public static string FormatMode(WeatherAlertOverlayMode mode)
        => mode switch
        {
            WeatherAlertOverlayMode.CutIn => "cutin",
            WeatherAlertOverlayMode.Ticker => "ticker",
            _ => "off"
        };

    public WeatherAlertOverlayMode Mode
        => ParseMode(FinTvRuntime.Current?.Configuration.WeatherAlertOverlayMode);

    public WeatherAlertOverlayMode EffectiveMode
    {
        get
        {
            var simulation = GetActiveSimulation();
            return simulation is null ? Mode : simulation.Mode;
        }
    }

    public TimeSpan CutInInterval
    {
        get
        {
            var minutes = FinTvRuntime.Current?.Configuration.WeatherAlertCutInIntervalMinutes ?? 15;
            return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 180));
        }
    }

    public TimeSpan CutInDuration
    {
        get
        {
            var seconds = FinTvRuntime.Current?.Configuration.WeatherAlertCutInDurationSeconds ?? 20;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 120));
        }
    }

    public bool AppliesTo(Channel channel)
        => channel.ContentType is not ChannelContentType.Weather and not ChannelContentType.News;

    public bool AllowsTicker(Channel channel)
        => AppliesTo(channel) && !PastTenseNewsCatalog.IsPastTenseNewsChannel(channel);

    public async Task<IReadOnlyList<WeatherAlert>> GetActiveAlertsAsync(CancellationToken cancellationToken)
    {
        var simulation = GetActiveSimulation();
        if (simulation is not null)
        {
            return simulation.Alerts;
        }

        try
        {
            var snap = await GetSnapshotAsync(cancellationToken);
            return snap.Alerts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    public WeatherSnapshot OverlayAlerts(WeatherSnapshot snap)
    {
        var simulation = GetActiveSimulation();
        if (simulation is null)
        {
            return snap;
        }

        return CloneWithAlerts(snap, simulation.Alerts);
    }

    public async Task<WeatherAlertTestPreview> StartTestAsync(
        WeatherAlertOverlayMode mode,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (mode is not WeatherAlertOverlayMode.CutIn and not WeatherAlertOverlayMode.Ticker)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Pick the alerts screen or scrolling text to test.");
        }

        var alerts = SampleAlerts();
        var armFor = duration > TimeSpan.FromMinutes(2) ? duration : TimeSpan.FromMinutes(2);
        var simulation = new AlertOverlaySimulation(Guid.NewGuid(), mode, DateTime.UtcNow + armFor, duration, alerts);
        lock (_gate)
        {
            _simulation = simulation;
        }

        var ticker = FormatTickerText(alerts);
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            await WriteTickerFileAsync(ticker, cancellationToken);
        }

        BeginBackgroundPrerender();

        byte[] jpeg;
        try
        {
            jpeg = await RenderHazardsPreviewAsync(alerts, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            jpeg = [];
        }

        return new WeatherAlertTestPreview(mode, duration, alerts, ticker, jpeg);
    }

    public bool HasActiveTest => GetActiveSimulation() is not null;

    public bool StopTest()
    {
        lock (_gate)
        {
            if (_simulation is null)
            {
                return false;
            }

            _simulation = null;
            _prerenderKey = null;
            _prerenderTask = null;
            return true;
        }
    }

    public WeatherSnapshot? TrySimulationSnapshot()
    {
        var simulation = GetActiveSimulation();
        return simulation is null ? null : PreviewSnapshot(simulation.Alerts);
    }

    public TimeSpan CutInDurationForStream
    {
        get
        {
            var simulation = GetActiveSimulation();
            if (simulation is not null && simulation.Mode is WeatherAlertOverlayMode.CutIn or WeatherAlertOverlayMode.Ticker)
            {
                return TimeSpan.FromSeconds(Math.Clamp(simulation.CutInDuration.TotalSeconds, 5, 120));
            }

            return CutInDuration;
        }
    }

    public async Task<bool> ShouldCutInNowAsync(
        Channel channel,
        WeatherAlertCutInSession session,
        CancellationToken cancellationToken)
    {
        if (!AppliesTo(channel))
        {
            return false;
        }

        if (EffectiveMode == WeatherAlertOverlayMode.Ticker && !AllowsTicker(channel))
        {
            return false;
        }

        if (EffectiveMode is not WeatherAlertOverlayMode.CutIn and not WeatherAlertOverlayMode.Ticker)
        {
            return false;
        }

        var simulation = GetActiveSimulation();
        if (simulation is not null
            && simulation.Mode == EffectiveMode
            && session.PlayedTestId != simulation.Id)
        {
            return true;
        }

        var alerts = await GetActiveAlertsAsync(cancellationToken);
        if (alerts.Count == 0)
        {
            return false;
        }

        return session.SecondsUntilNext(CutInInterval) <= 2;
    }

    public async Task<double> CapMediaDurationAsync(
        Channel channel,
        WeatherAlertCutInSession session,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (EffectiveMode is not WeatherAlertOverlayMode.CutIn and not WeatherAlertOverlayMode.Ticker
            || !AppliesTo(channel)
            || durationSeconds <= 2)
        {
            return durationSeconds;
        }

        if (EffectiveMode == WeatherAlertOverlayMode.Ticker && !AllowsTicker(channel))
        {
            return durationSeconds;
        }

        var alerts = await GetActiveAlertsAsync(cancellationToken);
        if (alerts.Count == 0)
        {
            return durationSeconds;
        }

        var until = session.SecondsUntilNext(CutInInterval);
        if (until <= 2)
        {
            return durationSeconds;
        }

        return Math.Max(2, Math.Min(durationSeconds, until));
    }

    public void MarkCutInComplete(WeatherAlertCutInSession session)
    {
        session.LastCutInUtc = DateTime.UtcNow;
        var simulation = GetActiveSimulation();
        if (simulation is not null)
        {
            session.PlayedTestId = simulation.Id;
        }
    }

    public void BeginBackgroundPrerender()
    {
        var mode = EffectiveMode;
        if (mode is not WeatherAlertOverlayMode.Ticker and not WeatherAlertOverlayMode.CutIn)
        {
            return;
        }

        lock (_gate)
        {
            var simulation = _simulation;
            var key = mode + "|" + (simulation?.Id.ToString("N") ?? "live");
            if (_prerenderKey == key && _prerenderTask is not null && !_prerenderTask.IsFaulted)
            {
                return;
            }

            _prerenderKey = key;
            var capturedMode = mode;
            _prerenderTask = Task.Run(() => PrerenderActiveModeAsync(capturedMode, CancellationToken.None));
        }
    }

    public async Task<string?> GetPrerenderedGraphicAsync(CancellationToken cancellationToken)
    {
        BeginBackgroundPrerender();
        Task<string?>? task;
        lock (_gate)
        {
            task = _prerenderTask;
        }

        if (task is null)
        {
            return null;
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "EBS graphic prerender failed");
            return null;
        }
    }

    public async Task<string?> PrepareTickerFileAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (EffectiveMode != WeatherAlertOverlayMode.Ticker || !AllowsTicker(channel))
        {
            return null;
        }

        return await GetPrerenderedGraphicAsync(cancellationToken);
    }

    public async Task<string?> PrepareFullscreenFileAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (EffectiveMode != WeatherAlertOverlayMode.CutIn || !AppliesTo(channel))
        {
            return null;
        }

        return await GetPrerenderedGraphicAsync(cancellationToken);
    }

    private async Task WriteTickerFileAsync(string text, CancellationToken cancellationToken)
    {
        var folder = FinTvRuntime.Current?.WeatherStarFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "alert-ticker.txt"), text, Encoding.UTF8, cancellationToken);
    }

    private async Task<string?> PrerenderActiveModeAsync(WeatherAlertOverlayMode mode, CancellationToken cancellationToken)
    {
        var alerts = await GetActiveAlertsAsync(cancellationToken);
        var ticker = FormatTickerText(alerts);
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        await WriteTickerFileAsync(ticker, cancellationToken);
        var folder = FinTvRuntime.Current?.WeatherStarFolder;
        var font = _assets.Star4000FontPath();
        var ffmpeg = _ffmpeg.EncoderPath;
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(font) || string.IsNullOrWhiteSpace(ffmpeg))
        {
            _logger.LogWarning("EBS prerender skipped; missing weatherstar folder, Star4000.ttf, or ffmpeg");
            return null;
        }

        Directory.CreateDirectory(folder);
        if (mode == WeatherAlertOverlayMode.Ticker)
        {
            var strip = EbsService.ResolveGraphic(EbsService.StripFileName);
            if (string.IsNullOrWhiteSpace(strip))
            {
                _logger.LogWarning("EBS ticker skipped; {File} was not found in the logo set", EbsService.StripFileName);
                return null;
            }

            var textPath = Path.Combine(folder, "alert-ticker.txt");
            var output = Path.Combine(folder, "alert-ticker.png");
            var barH = TickerBarHeight(PrerenderHeight);
            var fontSize = TickerFontSize(PrerenderHeight);
            var canvasW = Even(Math.Max(PrerenderWidth, PrerenderWidth + (int)(ticker.Length * fontSize * 0.55)));
            var vf =
                $"scale=-2:{barH},pad={canvasW}:{barH}:0:(oh-ih)/2:color=0xc41e3a," +
                $"drawtext=fontfile='{EscapeFilterPath(font)}':textfile='{EscapeFilterPath(textPath)}':expansion=none:" +
                $"fontcolor=white:fontsize={fontSize}:x=48:y=(h-text_h)/2";
            return await RunStillPrerenderAsync(ffmpeg, strip, vf, output, cancellationToken);
        }

        var fullscreen = EbsService.ResolveGraphic(EbsService.FullscreenFileName);
        if (string.IsNullOrWhiteSpace(fullscreen))
        {
            _logger.LogWarning("EBS alert screen skipped; {File} was not found in the logo set", EbsService.FullscreenFileName);
            return null;
        }

        var block = FormatFullscreenText(alerts);
        var blockPath = Path.Combine(folder, "alert-fullscreen.txt");
        await File.WriteAllTextAsync(blockPath, block, Encoding.UTF8, cancellationToken);
        var fullOut = Path.Combine(folder, "alert-fullscreen.png");
        var fullVf =
            $"scale={PrerenderWidth}:{PrerenderHeight}:force_original_aspect_ratio=increase," +
            $"crop={PrerenderWidth}:{PrerenderHeight}," +
            $"drawtext=fontfile='{EscapeFilterPath(font)}':textfile='{EscapeFilterPath(blockPath)}':expansion=none:" +
            $"fontcolor=white:fontsize=42:x=96:y=h*0.28:line_spacing=14";
        return await RunStillPrerenderAsync(ffmpeg, fullscreen, fullVf, fullOut, cancellationToken);
    }

    private async Task<string?> RunStillPrerenderAsync(
        string ffmpegPath,
        string inputPath,
        string videoFilter,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var error = new StringBuilder();
        var args = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-loop", "1",
            "-i", inputPath,
            "-vf", videoFilter,
            "-frames:v", "1",
            "-an",
            outputPath
        };
        try
        {
            var result = await Cli.Wrap(ffmpegPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error))
                .ExecuteAsync(cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                _logger.LogWarning(
                    "EBS prerender ffmpeg exited {Code}: {Error}",
                    result.ExitCode,
                    error.ToString().Trim());
                return null;
            }

            return outputPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "EBS prerender ffmpeg failed");
            return null;
        }
    }

    private static int TickerBarHeight(int height)
    {
        var barH = Math.Max(52, height / 18);
        return barH + (barH & 1);
    }

    private static int TickerFontSize(int height)
        => Math.Max(22, height / 42);

    private static int Even(int value)
        => value + (value & 1);

    private static string EscapeFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string FormatFullscreenText(IReadOnlyList<WeatherAlert> alerts)
    {
        var lines = new List<string> { "WEATHER ALERT" };
        foreach (var alert in alerts.Take(4))
        {
            var eventName = Compact(alert.Event);
            if (!string.IsNullOrWhiteSpace(eventName))
            {
                lines.Add(eventName.ToUpperInvariant());
            }

            var detail = Compact(string.IsNullOrWhiteSpace(alert.Headline) ? alert.Description : alert.Headline);
            if (!string.IsNullOrWhiteSpace(detail) && !detail.Equals(eventName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var wrap in WrapText(detail, 52).Take(4))
                {
                    lines.Add(wrap);
                }
            }
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length == 0)
            {
                line.Append(word);
                continue;
            }

            if (line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
                line.Append(word);
            }
            else
            {
                line.Append(' ').Append(word);
            }
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private async Task<string?> BuildTickerTextAsync(CancellationToken cancellationToken)
    {
        var alerts = await GetActiveAlertsAsync(cancellationToken);
        return FormatTickerText(alerts);
    }

    private static string? FormatTickerText(IReadOnlyList<WeatherAlert> alerts)
    {
        if (alerts.Count == 0)
        {
            return null;
        }

        var parts = new List<string> { "WEATHER ALERT" };
        foreach (var alert in alerts.Take(5))
        {
            var eventName = Compact(alert.Event);
            var detail = Compact(string.IsNullOrWhiteSpace(alert.Headline) ? alert.Description : alert.Headline);
            if (string.IsNullOrWhiteSpace(eventName) && string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }

            parts.Add(string.IsNullOrWhiteSpace(detail) || detail.Equals(eventName, StringComparison.OrdinalIgnoreCase)
                ? eventName
                : $"{eventName}: {detail}");
        }

        if (parts.Count < 2)
        {
            return null;
        }

        var body = string.Join("   •   ", parts);
        var text = $"{body}     {body}";
        return text.Length > 900 ? text[..900] : text;
    }

    private AlertOverlaySimulation? GetActiveSimulation()
    {
        lock (_gate)
        {
            var simulation = _simulation;
            if (simulation is null || simulation.UntilUtc <= DateTime.UtcNow)
            {
                if (simulation is not null)
                {
                    _simulation = null;
                }

                return null;
            }

            return simulation;
        }
    }

    private async Task<byte[]> RenderHazardsPreviewAsync(
        IReadOnlyList<WeatherAlert> alerts,
        CancellationToken cancellationToken)
    {
        WeatherSnapshot snap;
        try
        {
            snap = CloneWithAlerts(await GetSnapshotAsync(cancellationToken), alerts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snap = PreviewSnapshot(alerts);
        }

        var skin = WeatherStarChannelService.ResolveConfiguredVariant();
        return _compositor.RenderJpeg(snap, WeatherStarScreen.Hazards, skin, 854, 480, scanlines: false, radarIndex: 0);
    }

    private static WeatherSnapshot PreviewSnapshot(IReadOnlyList<WeatherAlert> alerts)
    {
        var location = WeatherStarChannelService.ResolveDefaultLocationQuery();
        return new WeatherSnapshot
        {
            Place = new GeoPlace
            {
                Query = location,
                DisplayName = location,
                Latitude = 0,
                Longitude = 0,
                Timezone = TimeZoneInfo.Local.Id
            },
            IsUnitedStates = true,
            Backend = "test",
            UseMetric = false,
            Alerts = alerts,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private static WeatherSnapshot CloneWithAlerts(WeatherSnapshot snap, IReadOnlyList<WeatherAlert> alerts)
        => new()
        {
            Place = snap.Place,
            IsUnitedStates = snap.IsUnitedStates,
            Backend = snap.Backend,
            UseMetric = snap.UseMetric,
            Current = snap.Current,
            Hourly = snap.Hourly,
            Daily = snap.Daily,
            Alerts = alerts,
            Radar = snap.Radar,
            Observations = snap.Observations,
            Periods = snap.Periods,
            Regional = snap.Regional,
            Travel = snap.Travel,
            SpcOutlook = snap.SpcOutlook,
            FetchedAt = snap.FetchedAt
        };

    private static IReadOnlyList<WeatherAlert> SampleAlerts()
        =>
        [
            new WeatherAlert
            {
                Event = "Severe Thunderstorm Watch",
                Headline = "SEVERE THUNDERSTORM WATCH IN EFFECT UNTIL 8 PM CDT",
                Description = "THIS IS A CHANNELFLOW TEST. A Severe Thunderstorm Watch is in effect. Be prepared to move to shelter if a warning is issued. This sample is not a real National Weather Service product.",
                Severity = "Severe"
            }
        ];

    private async Task<WeatherSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var location = WeatherStarChannelService.ResolveDefaultLocationQuery();
        var source = WeatherDataClient.ParseSource(config?.WeatherSource);
        var useMetric = WeatherStarChannelService.PermalinkUsesMetricUnits(config?.WeatherStarPermalinkQuery);
        return await _weather.GetSnapshotAsync(location, source, useMetric, cancellationToken);
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed class WeatherAlertCutInSession
{
    public DateTime LastCutInUtc { get; set; } = DateTime.UtcNow;

    public Guid PlayedTestId { get; set; }

    public double SecondsUntilNext(TimeSpan interval)
    {
        var due = LastCutInUtc + interval;
        return Math.Max(0, (due - DateTime.UtcNow).TotalSeconds);
    }
}

public sealed record AlertOverlaySimulation(
    Guid Id,
    WeatherAlertOverlayMode Mode,
    DateTime UntilUtc,
    TimeSpan CutInDuration,
    IReadOnlyList<WeatherAlert> Alerts);

public sealed record WeatherAlertTestPreview(
    WeatherAlertOverlayMode Mode,
    TimeSpan Duration,
    IReadOnlyList<WeatherAlert> Alerts,
    string? TickerText,
    byte[] HazardsJpeg);
