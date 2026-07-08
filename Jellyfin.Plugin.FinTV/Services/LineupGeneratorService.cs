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
    private readonly HolidayChannelService _holidays;

    public LineupGeneratorService(
        FinTvDbContext db,
        LineupService lineupService,
        SmartSelectionService smartSelection,
        CommercialService commercialService,
        ChannelService channelService,
        HolidayChannelService holidays)
    {
        _db = db;
        _lineupService = lineupService;
        _smartSelection = smartSelection;
        _commercialService = commercialService;
        _channelService = channelService;
        _holidays = holidays;
    }

    public async Task BuildPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode = PlayoutBuildMode.ReplaceWindow,
        CancellationToken cancellationToken = default)
    {
        if (channel.ContentType == ChannelContentType.Weather)
        {
            await BuildWeatherPlayoutAsync(channel, startUtc, endUtc, mode, cancellationToken);
            return;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(Plugin.Instance?.Configuration.ScheduleTimeZone ?? "America/New_York");
        var anchor = await _channelService.GetAnchorAsync<PlayoutAnchorState>(channel.Id, cancellationToken)
            ?? new PlayoutAnchorState();

        if (mode == PlayoutBuildMode.ReplaceWindow)
        {
            var existing = await _db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Start >= startUtc && p.Start < endUtc)
                .ToListAsync(cancellationToken);

            _db.PlayoutItems.RemoveRange(existing);
        }

        var cursor = startUtc;
        while (cursor < endUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(cursor, tz);
            var date = DateOnly.FromDateTime(local);
            var slots = await _lineupService.ResolveSlotsForDateAsync(channel.Id, date, cancellationToken);
            var slotIndex = (local.Hour * 60 + local.Minute) / 30;

            if (IsSlotConsumedByEarlierSpan(slots, slotIndex))
            {
                cursor = cursor.AddMinutes(30);
                continue;
            }

            var slot = slots.FirstOrDefault(s => s.SlotIndex == slotIndex);
            if (slot is null || slot.Candidates.Count == 0)
            {
                cursor = cursor.AddMinutes(30);
                continue;
            }

            var spanSlots = Math.Clamp(slot.SpanSlots, 1, 8);
            var slotStartLocal = local.Date.AddMinutes(slotIndex * 30);
            var blockEndLocal = slotStartLocal.AddMinutes(30 * spanSlots);
            var slotStart = TimeZoneInfo.ConvertTimeToUtc(slotStartLocal, tz);
            var blockEnd = TimeZoneInfo.ConvertTimeToUtc(blockEndLocal, tz);

            if (blockEnd <= cursor)
            {
                cursor = blockEnd;
                continue;
            }

            if (slotStart < cursor)
            {
                slotStart = cursor;
            }

            if (_holidays.IsHolidayChannel(channel) && _holidays.GetActiveHoliday(date) is null)
            {
                await AddHolidayOfflineBlockAsync(channel, slotStart, blockEnd, cancellationToken);
                cursor = blockEnd;
                continue;
            }

            var picked = await _smartSelection.PickCandidateAsync(channel, slot, date, anchor, cancellationToken);
            if (picked is null)
            {
                cursor = blockEnd;
                continue;
            }

            var contentStart = slotStart;
            var maxBlockDuration = blockEnd - contentStart;
            var contentEnd = blockEnd;
            if (picked.Duration > TimeSpan.Zero && picked.Duration < maxBlockDuration)
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

            cursor = blockEnd;
        }

        await _channelService.SaveAnchorAsync(channel.Id, anchor, cancellationToken);
        await _db.Channels
            .Where(c => c.Id == channel.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.LastPlayoutBuiltAt, DateTime.UtcNow),
                cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsSlotConsumedByEarlierSpan(IReadOnlyList<LineupSlot> slots, int slotIndex)
    {
        foreach (var slot in slots)
        {
            if (slot.SlotIndex >= slotIndex || slot.SpanSlots <= 1)
            {
                continue;
            }

            if (slotIndex >= slot.SlotIndex && slotIndex < slot.SlotIndex + slot.SpanSlots)
            {
                return true;
            }
        }

        return false;
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

    private async Task BuildWeatherPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        if (mode == PlayoutBuildMode.ReplaceWindow)
        {
            var existing = await _db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish > startUtc && p.Start < endUtc)
                .ToListAsync(cancellationToken);

            _db.PlayoutItems.RemoveRange(existing);
        }

        var appendStart = startUtc;
        if (mode == PlayoutBuildMode.ExtendHorizon)
        {
            var latestFinish = await _db.PlayoutItems
                .Where(p =>
                    p.ChannelId == channel.Id
                    && p.IsVirtual
                    && p.VirtualSource == VirtualContentSource.WeatherStar
                    && p.Finish > startUtc)
                .Select(p => (DateTime?)p.Finish)
                .MaxAsync(cancellationToken);

            if (latestFinish.HasValue && latestFinish.Value > appendStart)
            {
                appendStart = latestFinish.Value;
            }
        }

        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        foreach (var (blockStart, blockEnd) in WeatherLineupHelper.BuildHourBlocksUtc(appendStart, endUtc, tz))
        {
            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                Start = blockStart,
                Finish = blockEnd,
                Title = WeatherLineupHelper.FormatHourTitle(blockStart, tz),
                IsVirtual = true,
                VirtualSource = VirtualContentSource.WeatherStar
            });
        }

        channel.LastPlayoutBuiltAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private Task AddHolidayOfflineBlockAsync(
        Channel channel,
        DateTime start,
        DateTime finish,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var offline = _holidays.FindOfflineMediaItem();
        _db.PlayoutItems.Add(new PlayoutItem
        {
            ChannelId = channel.Id,
            JellyfinItemId = offline?.Id,
            Start = start,
            Finish = finish,
            Title = offline?.Name ?? "The Holiday Channel - Off Season"
        });

        return Task.CompletedTask;
    }
}
