using System.Collections.Concurrent;
using System.Text;
using CliWrap;
using FinTv.Data;
using FinTv.Domain;
using FinTv.News;
using FinTv.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class StreamService
{
    private readonly ConcurrentDictionary<Guid, int> _activeStreams = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FfmpegCommandBuilder _ffmpeg;
    private readonly WeatherAlertOverlayService _weatherAlerts;
    private readonly ILogger<StreamService> _logger;
    private readonly IFfmpegLocator _mediaEncoder;

    public StreamService(
        IServiceScopeFactory scopeFactory,
        FfmpegCommandBuilder ffmpeg,
        WeatherAlertOverlayService weatherAlerts,
        ILogger<StreamService> logger,
        IFfmpegLocator mediaEncoder)
    {
        _scopeFactory = scopeFactory;
        _ffmpeg = ffmpeg;
        _weatherAlerts = weatherAlerts;
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    public async Task StreamChannelAsync(Guid channelId, Stream output, CancellationToken cancellationToken)
    {
        using var streamLease = TrackStream(channelId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
        var weather = scope.ServiceProvider.GetRequiredService<WeatherStarChannelService>();
        var ebs = scope.ServiceProvider.GetRequiredService<EbsService>();
        var youtubeCommercials = scope.ServiceProvider.GetRequiredService<YouTubeCommercialStreamService>();
        var holidays = scope.ServiceProvider.GetRequiredService<HolidayChannelService>();

        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            throw new InvalidOperationException("Channel not found.");
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            await StreamUntilCanceledAsync(
                "Weather",
                channel.Name,
                () => weather.StreamAsync(channel, output, cancellationToken),
                cancellationToken);
            return;
        }

        if (channel.ContentType == ChannelContentType.News)
        {
            var news = scope.ServiceProvider.GetRequiredService<NewsChannelService>();
            await StreamUntilCanceledAsync(
                "News",
                channel.Name,
                () => news.StreamAsync(channel, output, cancellationToken),
                cancellationToken);
            return;
        }

        var ffmpegPath = _mediaEncoder.EncoderPath;
        var alertSession = new WeatherAlertCutInSession();

        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            if (await _weatherAlerts.ShouldCutInNowAsync(channel, alertSession, cancellationToken))
            {
                try
                {
                    await weather.StreamHazardsCutInAsync(channel, output, _weatherAlerts.CutInDurationForStream, cancellationToken);
                    _weatherAlerts.MarkCutInComplete(alertSession);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Weather alert cut-in failed for {Channel}", channel.Name);
                    _weatherAlerts.MarkCutInComplete(alertSession);
                }

                continue;
            }

            var current = await GetCurrentItemAsync(channelId, cancellationToken);
            if (current is not null)
            {
                try
                {
                    if (current.IsVirtual && current.VirtualSource == VirtualContentSource.MusicArtSlide)
                    {
                        await StreamMusicItemAsync(channel, current, catalog, ffmpegPath, output, cancellationToken);
                    }
                    else if (current.CommercialId.HasValue)
                    {
                        await StreamCommercialItemAsync(channel, current, catalog, holidays, youtubeCommercials, ffmpegPath, output, cancellationToken);
                    }
                    else if (current.JellyfinItemId.HasValue)
                    {
                        await StreamMediaItemAsync(channel, current, catalog, holidays, ffmpegPath, output, alertSession, cancellationToken);
                    }
                    else
                    {
                        await WriteEbsAsync(channel, ebs, ffmpegPath, output, 180, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed streaming item {Title}", current.Title);
                    await WriteEbsAsync(channel, ebs, ffmpegPath, output, 120, cancellationToken);
                }
            }
            else
            {
                var ebsDuration = await GetEbsDurationSecondsAsync(channelId, cancellationToken);
                await WriteEbsAsync(channel, ebs, ffmpegPath, output, ebsDuration, cancellationToken);
            }

            await DelayIfStreamEndedImmediatelyAsync(channel.Name, started, cancellationToken);
        }
    }

    private async Task StreamUntilCanceledAsync(
        string kind,
        string channelName,
        Func<Task> stream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await stream();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Kind} stream failed for {Channel}; retrying in 5 seconds", kind, channelName);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            await DelayIfStreamEndedImmediatelyAsync(channelName, started, cancellationToken);
        }
    }

    private async Task DelayIfStreamEndedImmediatelyAsync(
        string channelName,
        DateTime startedUtc,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - startedUtc;
        if (elapsed >= TimeSpan.FromSeconds(3))
        {
            return;
        }

        _logger.LogWarning(
            "Stream ended after {ElapsedMs:0}ms for {Channel}; retrying in 5 seconds",
            elapsed.TotalMilliseconds,
            channelName);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public async Task<PlayoutItem?> GetCurrentItemAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var now = DateTime.UtcNow;
        return await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start <= now && p.Finish > now)
            .OrderByDescending(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public IReadOnlyList<ChannelStreamStatus> GetActiveStreams()
    {
        return _activeStreams
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new ChannelStreamStatus
            {
                ChannelId = kvp.Key,
                ViewerCount = kvp.Value
            })
            .ToList();
    }

    public int GetActiveStreamCount(Guid channelId)
    {
        return _activeStreams.TryGetValue(channelId, out var count) ? count : 0;
    }

    private StreamLease TrackStream(Guid channelId)
    {
        _activeStreams.AddOrUpdate(channelId, 1, (_, count) => count + 1);
        return new StreamLease(this, channelId);
    }

    private void ReleaseStream(Guid channelId)
    {
        _activeStreams.AddOrUpdate(channelId, 0, (_, count) => Math.Max(0, count - 1));
        if (_activeStreams.TryGetValue(channelId, out var remaining) && remaining == 0)
        {
            _activeStreams.TryRemove(channelId, out _);
        }
    }

    private sealed class StreamLease : IDisposable
    {
        private readonly StreamService _service;
        private readonly Guid _channelId;
        private int _disposed;

        public StreamLease(StreamService service, Guid channelId)
        {
            _service = service;
            _channelId = channelId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _service.ReleaseStream(_channelId);
        }
    }

    private async Task<double> GetEbsDurationSecondsAsync(Guid channelId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var now = DateTime.UtcNow;
        var nextStart = await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start > now)
            .OrderBy(p => p.Start)
            .Select(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);

        if (nextStart == default)
        {
            return 600;
        }

        return Math.Clamp((nextStart - now).TotalSeconds, 30, 600);
    }

    private async Task StreamMediaItemAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        HolidayChannelService holidays,
        string ffmpegPath,
        Stream output,
        WeatherAlertCutInSession alertSession,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var mediaItem = libraryManager.GetItemById(item.JellyfinItemId!.Value);
        if (mediaItem is null)
        {
            throw new InvalidOperationException($"Media item {item.JellyfinItemId} not found.");
        }

        var inputPath = catalog.GetMediaPath(mediaItem);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Media path missing for {item.Title}.");
        }

        var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
        var duration = Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
        duration = await _weatherAlerts.CapMediaDurationAsync(channel, alertSession, duration, cancellationToken);
        var bugPath = ResolveBugPath(channel, item.Start, holidays);
        var headline = PastTenseNewsCatalog.IsPastTenseNewsChannel(channel) ? item.Title : null;
        var tickerPath = await _weatherAlerts.PrepareTickerFileAsync(channel, cancellationToken);
        var args = _ffmpeg.BuildMediaCommand(channel, inputPath, offset, duration, bugPath, headline, tickerPath);

        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task StreamCommercialItemAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        HolidayChannelService holidays,
        YouTubeCommercialStreamService youtubeCommercials,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var commercial = await db.Commercials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == item.CommercialId, cancellationToken);

        if (commercial is null)
        {
            throw new InvalidOperationException($"Commercial {item.CommercialId} not found.");
        }

        if (commercial.Source == CommercialSource.Jellyfin)
        {
            var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
            var mediaItem = libraryManager.GetItemById(commercial.JellyfinItemId);
            if (mediaItem is null)
            {
                throw new InvalidOperationException($"Media item {commercial.JellyfinItemId} not found.");
            }

            var inputPath = catalog.GetMediaPath(mediaItem);
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                throw new FileNotFoundException($"Media path missing for {commercial.Title}.");
            }

            var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
            var duration = Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
            var bugPath = ResolveBugPath(channel, item.Start, holidays);
            var args = _ffmpeg.BuildMediaCommand(channel, inputPath, offset, duration, bugPath);
            await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
            return;
        }

        await youtubeCommercials.StreamCommercialAsync(
            channel,
            commercial,
            _ffmpeg,
            ffmpegPath,
            Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds),
            output,
            cancellationToken);
    }

    private async Task StreamMusicItemAsync(
        Channel channel,
        PlayoutItem item,
        JellyfinCatalogService catalog,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var mediaItem = libraryManager.GetItemById(item.JellyfinItemId!.Value);
        if (mediaItem is null)
        {
            throw new InvalidOperationException($"Music item {item.JellyfinItemId} not found.");
        }

        var inputPath = catalog.GetMediaPath(mediaItem);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Music path missing for {item.Title}.");
        }

        var albumArt = catalog.GetPrimaryImagePath(mediaItem);
        var tickerPath = await _weatherAlerts.PrepareTickerFileAsync(channel, cancellationToken);
        var args = _ffmpeg.BuildMusicCommand(channel, inputPath, albumArt, tickerPath);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task WriteEbsAsync(
        Channel channel,
        EbsService ebs,
        string ffmpegPath,
        Stream output,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var plan = ebs.CreatePlaybackPlan(channel, durationSeconds);
        var args = _ffmpeg.BuildEbsCommand(channel, plan);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private static string? ResolveBugPath(Channel channel, DateTime scheduleUtc, HolidayChannelService holidays)
    {
        if (channel.BugPlacement == BugPlacementMode.None)
        {
            return null;
        }

        if (holidays.IsHolidayChannel(channel))
        {
            var date = holidays.GetScheduleDateUtc(scheduleUtc);
            return holidays.ResolveEffectiveLogoPath(channel, date);
        }

        return channel.ChannelLogoPath;
    }

    private static async Task RunFfmpegToStreamAsync(string ffmpegPath, IReadOnlyList<string> args, Stream output, CancellationToken cancellationToken)
    {
        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
            .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(new StringBuilder()))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }
}
