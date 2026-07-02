using System.Collections.Concurrent;
using System.Text;
using CliWrap;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class StreamService
{
    private readonly ConcurrentDictionary<Guid, int> _activeStreams = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FfmpegCommandBuilder _ffmpeg;
    private readonly ILogger<StreamService> _logger;
    private readonly IMediaEncoder _mediaEncoder;

    public StreamService(
        IServiceScopeFactory scopeFactory,
        FfmpegCommandBuilder ffmpeg,
        ILogger<StreamService> logger,
        IMediaEncoder mediaEncoder)
    {
        _scopeFactory = scopeFactory;
        _ffmpeg = ffmpeg;
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

        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            throw new InvalidOperationException("Channel not found.");
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await weather.StreamAsync(channel, output, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Weather stream failed for {Channel}; retrying in 5 seconds", channel.Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }

            return;
        }

        var ffmpegPath = _mediaEncoder.EncoderPath;

        while (!cancellationToken.IsCancellationRequested)
        {
            var current = await GetCurrentItemAsync(channelId, cancellationToken);
            if (current is not null)
            {
                try
                {
                    if (current.IsVirtual && current.VirtualSource == VirtualContentSource.MusicArtSlide)
                    {
                        await StreamMusicItemAsync(channel, current, catalog, ffmpegPath, output, cancellationToken);
                    }
                    else if (current.JellyfinItemId.HasValue)
                    {
                        await StreamMediaItemAsync(channel, current, catalog, ffmpegPath, output, cancellationToken);
                    }
                    else
                    {
                        await WriteEbsAsync(channel, ebs, catalog, ffmpegPath, output, 180, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed streaming item {Title}", current.Title);
                    await WriteEbsAsync(channel, ebs, catalog, ffmpegPath, output, 120, cancellationToken);
                }

                continue;
            }

            var ebsDuration = await GetEbsDurationSecondsAsync(channelId, cancellationToken);
            await WriteEbsAsync(channel, ebs, catalog, ffmpegPath, output, ebsDuration, cancellationToken);
        }
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
        string ffmpegPath,
        Stream output,
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
        var args = _ffmpeg.BuildMediaCommand(channel, inputPath, offset, duration, catalog.GetPrimaryImagePath(mediaItem));

        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
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
        var args = _ffmpeg.BuildMusicCommand(channel, inputPath, albumArt);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task WriteEbsAsync(
        Channel channel,
        EbsService ebs,
        JellyfinCatalogService catalog,
        string ffmpegPath,
        Stream output,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var slatePath = ebs.ResolveRandomSlatePath();
        if (string.IsNullOrWhiteSpace(slatePath))
        {
            _logger.LogWarning("No EBS slate found for channel {Channel}; using text slate", channel.Name);
            var fallback = _ffmpeg.BuildOfflineSlateCommand(channel);
            await RunFfmpegToStreamAsync(ffmpegPath, fallback, output, cancellationToken);
            return;
        }

        string? audioPath = null;
        var track = ebs.PickBackgroundMusicTrack();
        if (track is not null)
        {
            audioPath = catalog.GetMediaPath(track);
        }

        var args = _ffmpeg.BuildEbsCommand(channel, slatePath, audioPath, durationSeconds);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
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
