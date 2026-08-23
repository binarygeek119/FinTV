using System.Text;
using FinTv.Domain;
using FinTv.Weather;

namespace FinTv.Services;

public sealed class WeatherAlertOverlayService
{
    private readonly WeatherDataClient _weather;
    private readonly WeatherStarCompositor _compositor;
    private readonly object _gate = new();
    private AlertOverlaySimulation? _simulation;

    public WeatherAlertOverlayService(WeatherDataClient weather, WeatherStarCompositor compositor)
    {
        _weather = weather;
        _compositor = compositor;
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
        if (mode == WeatherAlertOverlayMode.Ticker && !string.IsNullOrWhiteSpace(ticker))
        {
            await WriteTickerFileAsync(ticker, cancellationToken);
        }

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
            if (simulation is not null && simulation.Mode == WeatherAlertOverlayMode.CutIn)
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
        if (EffectiveMode != WeatherAlertOverlayMode.CutIn || !AppliesTo(channel))
        {
            return false;
        }

        var simulation = GetActiveSimulation();
        if (simulation is not null && simulation.Mode == WeatherAlertOverlayMode.CutIn && session.PlayedTestId != simulation.Id)
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
        if (EffectiveMode != WeatherAlertOverlayMode.CutIn || !AppliesTo(channel) || durationSeconds <= 2)
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

    public async Task<string?> PrepareTickerFileAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (EffectiveMode != WeatherAlertOverlayMode.Ticker || !AllowsTicker(channel))
        {
            return null;
        }

        var text = await BuildTickerTextAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var folder = FinTvRuntime.Current?.WeatherStarFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "alert-ticker.txt");
        await File.WriteAllTextAsync(path, text, Encoding.UTF8, cancellationToken);
        return path;
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

        var config = FinTvRuntime.Current?.Configuration;
        var skin = string.Equals(config?.WeatherStarVariant, "ws3kp", StringComparison.OrdinalIgnoreCase)
            ? WeatherStarDockerVariant.Ws3kp
            : WeatherStarDockerVariant.Ws4kp;
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
