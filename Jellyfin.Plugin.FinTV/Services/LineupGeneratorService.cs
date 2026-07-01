using System.Text.Json;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class LineupGeneratorService
{
    private readonly FinTvDbContext _db;
    private readonly LineupService _lineupService;
    private readonly SmartSelectionService _smartSelection;
    private readonly CommercialService _commercialService;
    private readonly ChannelService _channelService;

    public LineupGeneratorService(
        FinTvDbContext db,
        LineupService lineupService,
        SmartSelectionService smartSelection,
        CommercialService commercialService,
        ChannelService channelService)
    {
        _db = db;
        _lineupService = lineupService;
        _smartSelection = smartSelection;
        _commercialService = commercialService;
        _channelService = channelService;
    }

    public async Task BuildPlayoutAsync(Channel channel, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        if (channel.ContentType == ChannelContentType.Weather)
        {
            await BuildWeatherPlayoutAsync(channel, startUtc, endUtc, cancellationToken);
            return;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(Plugin.Instance?.Configuration.ScheduleTimeZone ?? "America/New_York");
        var anchor = await _channelService.GetAnchorAsync<PlayoutAnchorState>(channel.Id, cancellationToken)
            ?? new PlayoutAnchorState();

        var existing = await _db.PlayoutItems
            .Where(p => p.ChannelId == channel.Id && p.Start >= startUtc && p.Start < endUtc)
            .ToListAsync(cancellationToken);

        _db.PlayoutItems.RemoveRange(existing);

        var cursor = startUtc;
        while (cursor < endUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(cursor, tz);
            var date = DateOnly.FromDateTime(local);
            var slots = await _lineupService.ResolveSlotsForDateAsync(channel.Id, date, cancellationToken);
            var slotIndex = (local.Hour * 60 + local.Minute) / 30;
            var slot = slots.FirstOrDefault(s => s.SlotIndex == slotIndex);
            if (slot is null)
            {
                cursor = cursor.AddMinutes(30);
                continue;
            }

            var slotStartLocal = local.Date.AddMinutes(slotIndex * 30);
            var slotEndLocal = slotStartLocal.AddMinutes(30);
            var slotStart = TimeZoneInfo.ConvertTimeToUtc(slotStartLocal, tz);
            var slotEnd = TimeZoneInfo.ConvertTimeToUtc(slotEndLocal, tz);

            if (slotEnd <= cursor)
            {
                cursor = slotEnd;
                continue;
            }

            if (slotStart < cursor)
            {
                slotStart = cursor;
            }

            var picked = await _smartSelection.PickCandidateAsync(channel, slot, date, anchor, cancellationToken);
            if (picked is null)
            {
                cursor = slotEnd;
                continue;
            }

            var contentStart = slotStart;
            var contentEnd = slotEnd;
            if (picked.Duration < TimeSpan.FromMinutes(30) && picked.Duration > TimeSpan.Zero)
            {
                contentEnd = contentStart.Add(picked.Duration);
            }

            if (channel.ContentType == ChannelContentType.Music && picked.JellyfinItemId.HasValue)
            {
                await AddMusicPlayoutItemAsync(channel, picked, contentStart, contentEnd, cancellationToken);
            }
            else
            {
                _db.PlayoutItems.Add(new PlayoutItem
                {
                    ChannelId = channel.Id,
                    JellyfinItemId = picked.JellyfinItemId,
                    Start = contentStart,
                    Finish = contentEnd,
                    Title = picked.Title,
                    IsVirtual = picked.IsVirtual,
                    VirtualSource = picked.VirtualSource
                });
            }

            await _commercialService.InsertCommercialsAsync(channel, picked, contentStart, contentEnd, cancellationToken);

            _db.PlayoutHistory.Add(new PlayoutHistoryEntry
            {
                ChannelId = channel.Id,
                JellyfinItemId = picked.JellyfinItemId,
                AiredAt = contentStart,
                Title = picked.Title
            });

            cursor = slotEnd;
        }

        await _channelService.SaveAnchorAsync(channel.Id, anchor, cancellationToken);
        channel.LastPlayoutBuiltAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task AddMusicPlayoutItemAsync(
        Channel channel,
        ResolvedCandidate picked,
        DateTime start,
        DateTime finish,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _db.PlayoutItems.Add(new PlayoutItem
        {
            ChannelId = channel.Id,
            JellyfinItemId = picked.JellyfinItemId,
            Start = start,
            Finish = finish,
            Title = picked.Title,
            IsVirtual = true,
            VirtualSource = VirtualContentSource.MusicArtSlide
        });

        await Task.CompletedTask;
    }

    private async Task BuildWeatherPlayoutAsync(Channel channel, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var existing = await _db.PlayoutItems
            .Where(p => p.ChannelId == channel.Id && p.Start >= startUtc && p.Start < endUtc)
            .ToListAsync(cancellationToken);

        _db.PlayoutItems.RemoveRange(existing);
        _db.PlayoutItems.Add(new PlayoutItem
        {
            ChannelId = channel.Id,
            Start = startUtc,
            Finish = endUtc,
            Title = "WeatherStar 4000",
            IsVirtual = true,
            VirtualSource = VirtualContentSource.WeatherStar
        });

        channel.LastPlayoutBuiltAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
