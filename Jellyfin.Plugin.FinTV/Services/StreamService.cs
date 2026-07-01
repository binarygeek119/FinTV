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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
        var weather = scope.ServiceProvider.GetRequiredService<WeatherStarChannelService>();

        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            throw new InvalidOperationException("Channel not found.");
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            await weather.StreamAsync(channel, output, cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;
        var batch = await db.PlayoutItems
            .Where(p => p.ChannelId == channelId && p.Finish > now)
            .OrderBy(p => p.Start)
            .Take(5)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            await WriteOfflineSlateAsync(channel, output, cancellationToken);
            return;
        }

        var ffmpegPath = _mediaEncoder.EncoderPath;

        foreach (var item in batch)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (item.Start > DateTime.UtcNow.AddMinutes(5))
            {
                break;
            }

            try
            {
                if (item.IsVirtual && item.VirtualSource == VirtualContentSource.MusicArtSlide)
                {
                    await StreamMusicItemAsync(channel, item, catalog, ffmpegPath, output, cancellationToken);
                }
                else if (item.JellyfinItemId.HasValue)
                {
                    await StreamMediaItemAsync(channel, item, catalog, ffmpegPath, output, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed streaming item {Title}", item.Title);
            }
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
            return;
        }

        var inputPath = catalog.GetMediaPath(mediaItem);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return;
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
            return;
        }

        var inputPath = catalog.GetMediaPath(mediaItem);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return;
        }

        var albumArt = catalog.GetPrimaryImagePath(mediaItem);
        var args = _ffmpeg.BuildMusicCommand(channel, inputPath, albumArt);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task WriteOfflineSlateAsync(Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var args = _ffmpeg.BuildOfflineSlateCommand(channel);
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
