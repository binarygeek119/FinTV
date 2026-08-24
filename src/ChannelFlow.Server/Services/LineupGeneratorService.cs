using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public class LineupGeneratorService
{
    private readonly FinTvDbContext _db;
    private readonly LineupService _lineupService;
    private readonly SmartSelectionService _smartSelection;
    private readonly CommercialService _commercialService;
    private readonly ChannelService _channelService;
    private readonly HolidayChannelService _holidays;
    private readonly GuideUpdateTracker _guideUpdates;

    public LineupGeneratorService(
        FinTvDbContext db,
        LineupService lineupService,
        SmartSelectionService smartSelection,
        CommercialService commercialService,
        ChannelService channelService,
        HolidayChannelService holidays,
        GuideUpdateTracker guideUpdates)
    {
        _db = db;
        _lineupService = lineupService;
        _smartSelection = smartSelection;
        _commercialService = commercialService;
        _channelService = channelService;
        _holidays = holidays;
        _guideUpdates = guideUpdates;
    }

    public async Task BuildPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode = PlayoutBuildMode.ReplaceWindow,
        CancellationToken cancellationToken = default)
    {
        if (channel.ContentType is ChannelContentType.Weather or ChannelContentType.News)
        {
            await BuildContinuousPlayoutAsync(channel, startUtc, endUtc, mode, cancellationToken);
            _guideUpdates.MarkUpdated();
            return;
        }

        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        var anchor = await _channelService.GetAnchorAsync<PlayoutAnchorState>(channel.Id, cancellationToken)
            ?? new PlayoutAnchorState();

        if (mode == PlayoutBuildMode.ReplaceWindow)
        {
            var existing = await _db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish > startUtc && p.Start < endUtc)
                .ToListAsync(cancellationToken);

            await RewindEpisodeCursorsAsync(existing, anchor, cancellationToken);
            _db.PlayoutItems.RemoveRange(existing);
        }

        var builtPrograms = new List<PlayoutItem>();

        var snapshot = await _lineupService.LoadResolutionSnapshotAsync(channel.Id, cancellationToken);
        var slotsByDate = new Dictionary<DateOnly, IReadOnlyList<LineupSlot>>();
        var cursor = startUtc;
        while (cursor < endUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(cursor, tz);
            var date = DateOnly.FromDateTime(local);
            if (!slotsByDate.TryGetValue(date, out var slots))
            {
                slots = _lineupService.ResolveSlotsForDate(snapshot, date);
                slotsByDate[date] = slots;
            }

            var slotIndex = (local.Hour * 60 + local.Minute) / 30;

            if (IsSlotConsumedByEarlierSpan(slots, slotIndex))
            {
                cursor = cursor.AddMinutes(30);
                continue;
            }

            var slot = slots.FirstOrDefault(s => s.SlotIndex == slotIndex);
            if (slot is null || slot.Candidates.Count == 0)
            {
                if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
                {
                    slot = CreatePastTenseNewsSlot(channel, slotIndex);
                }
                else if (channel.ContentType == ChannelContentType.TvShow
                    && NetworkSchedulePlanner.IsOvernightRerunSlot(slotIndex))
                {
                    var rerunStartLocal = local.Date.AddMinutes(slotIndex * 30);
                    var rerunEndLocal = rerunStartLocal.AddMinutes(30);
                    var rerunStart = TimeZoneInfo.ConvertTimeToUtc(rerunStartLocal, tz);
                    var rerunEnd = TimeZoneInfo.ConvertTimeToUtc(rerunEndLocal, tz);
                    if (rerunStart < cursor)
                    {
                        rerunStart = cursor;
                    }

                    var overnightEnd = await TryAddOvernightRerunAsync(
                        channel,
                        date,
                        tz,
                        rerunStart,
                        rerunEnd,
                        builtPrograms,
                        cancellationToken).ConfigureAwait(false);
                    if (overnightEnd is DateTime paddedOvernightEnd)
                    {
                        cursor = paddedOvernightEnd;
                        continue;
                    }

                    cursor = cursor.AddMinutes(30);
                    continue;
                }
                else
                {
                    cursor = cursor.AddMinutes(30);
                    continue;
                }
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

            if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
            {
                await PackPastTenseNewsBlockAsync(
                    channel,
                    CreatePastTenseNewsSlot(channel, slotIndex),
                    date,
                    anchor,
                    slotStart,
                    blockEnd,
                    cancellationToken);
                cursor = blockEnd;
                continue;
            }

            if (channel.ContentType == ChannelContentType.MusicVideo)
            {
                await PackMusicVideoBlockAsync(
                    channel,
                    slot,
                    date,
                    anchor,
                    slotStart,
                    blockEnd,
                    cancellationToken);
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
            var contentEnd = picked.Duration > TimeSpan.Zero
                ? contentStart.Add(picked.Duration)
                : blockEnd;
            var padUntil = ResolveSlotPadEnd(contentEnd, blockEnd, tz);

            if (channel.ContentType == ChannelContentType.Music && picked.JellyfinItemId.HasValue)
            {
                await AddMusicPlayoutItemAsync(channel, picked, contentStart, contentEnd, cancellationToken);
                await _commercialService.PadToSlotAsync(channel, contentEnd, padUntil, cancellationToken);
            }
            else
            {
                var scheduled = await _commercialService.ScheduleProgramWithBreaksAsync(
                    channel,
                    picked,
                    contentStart,
                    padUntil,
                    cancellationToken);
                builtPrograms.AddRange(scheduled.ProgramItems);
                padUntil = ResolveSlotPadEnd(scheduled.TimelineEnd, blockEnd, tz);
                if (padUntil > scheduled.TimelineEnd)
                {
                    await _commercialService.PadToSlotAsync(channel, scheduled.TimelineEnd, padUntil, cancellationToken);
                }
            }

            _db.PlayoutHistory.Add(new PlayoutHistoryEntry
            {
                ChannelId = channel.Id,
                JellyfinItemId = picked.JellyfinItemId,
                AiredAt = contentStart,
                Title = picked.Title
            });

            cursor = padUntil;
        }

        await _channelService.SaveAnchorAsync(channel.Id, anchor, cancellationToken);
        await _db.Channels
            .Where(c => c.Id == channel.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.LastPlayoutBuiltAt, DateTime.UtcNow),
                cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _guideUpdates.MarkUpdated();
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

    private async Task BuildContinuousPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        var source = channel.ContentType == ChannelContentType.News
            ? VirtualContentSource.News
            : VirtualContentSource.WeatherStar;

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
                    && p.VirtualSource == source
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
            var title = source == VirtualContentSource.News
                ? $"Headlines · {TimeZoneInfo.ConvertTimeFromUtc(blockStart, tz):h:mm tt}"
                : WeatherLineupHelper.FormatHourTitle(blockStart, tz);
            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                Start = blockStart,
                Finish = blockEnd,
                Title = title,
                IsVirtual = true,
                VirtualSource = source
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

    private static LineupSlot CreatePastTenseNewsSlot(Channel channel, int slotIndex)
        => new()
        {
            SlotIndex = slotIndex,
            SpanSlots = 1,
            Candidates =
            [
                new SlotCandidate
                {
                    Kind = SlotCandidateKind.FilterQuery,
                    FilterJson = string.IsNullOrWhiteSpace(channel.FilterJson) ? "{}" : channel.FilterJson,
                    Weight = 10,
                    SortOrder = 0
                }
            ]
        };

    private async Task PackPastTenseNewsBlockAsync(
        Channel channel,
        LineupSlot slot,
        DateOnly date,
        PlayoutAnchorState anchor,
        DateTime slotStart,
        DateTime blockEnd,
        CancellationToken cancellationToken)
    {
        var fillStart = slotStart;
        var packed = 0;
        while (fillStart < blockEnd - TimeSpan.FromSeconds(8) && packed < 24)
        {
            var picked = await _smartSelection.PickCandidateAsync(channel, slot, date, anchor, cancellationToken);
            if (picked is null)
            {
                break;
            }

            var clipDuration = picked.Duration > TimeSpan.Zero ? picked.Duration : TimeSpan.FromMinutes(4);
            var fillEnd = fillStart + clipDuration;
            if (fillEnd > blockEnd)
            {
                fillEnd = blockEnd;
            }

            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                JellyfinItemId = picked.JellyfinItemId,
                Start = fillStart,
                Finish = fillEnd,
                Title = picked.Title
            });
            _db.PlayoutHistory.Add(new PlayoutHistoryEntry
            {
                ChannelId = channel.Id,
                JellyfinItemId = picked.JellyfinItemId,
                AiredAt = fillStart,
                Title = picked.Title
            });
            await _commercialService.InsertCommercialsAsync(channel, picked, fillStart, fillEnd, cancellationToken);
            fillStart = fillEnd;
            packed++;
        }

        await _commercialService.PadToSlotAsync(channel, fillStart, blockEnd, cancellationToken);
    }

    private async Task PackMusicVideoBlockAsync(
        Channel channel,
        LineupSlot slot,
        DateOnly date,
        PlayoutAnchorState anchor,
        DateTime slotStart,
        DateTime blockEnd,
        CancellationToken cancellationToken)
    {
        var fillStart = slotStart;
        var packed = 0;
        while (fillStart < blockEnd - TimeSpan.FromSeconds(8) && packed < 24)
        {
            var picked = await _smartSelection.PickCandidateAsync(channel, slot, date, anchor, cancellationToken);
            if (picked is null)
            {
                break;
            }

            var clipDuration = picked.Duration > TimeSpan.Zero ? picked.Duration : TimeSpan.FromMinutes(4);
            var fillEnd = fillStart + clipDuration;
            if (fillEnd > blockEnd)
            {
                fillEnd = blockEnd;
            }

            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                JellyfinItemId = picked.JellyfinItemId,
                Start = fillStart,
                Finish = fillEnd,
                Title = picked.Title
            });
            _db.PlayoutHistory.Add(new PlayoutHistoryEntry
            {
                ChannelId = channel.Id,
                JellyfinItemId = picked.JellyfinItemId,
                AiredAt = fillStart,
                Title = picked.Title
            });
            await _commercialService.InsertCommercialsAsync(channel, picked, fillStart, fillEnd, cancellationToken);
            fillStart = fillEnd;
            packed++;
        }

        await _commercialService.PadToSlotAsync(channel, fillStart, blockEnd, cancellationToken);
    }

    private async Task RewindEpisodeCursorsAsync(
        List<PlayoutItem> removed,
        PlayoutAnchorState anchor,
        CancellationToken cancellationToken)
    {
        var episodeIds = removed
            .Where(p => p.JellyfinItemId.HasValue
                && !string.Equals(p.GuideGroup, "commercial", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.JellyfinItemId!.Value)
            .ToList();
        if (episodeIds.Count == 0 || anchor.SeriesEpisodeIndex.Count == 0)
        {
            return;
        }

        var seriesByEpisode = await _db.Episodes
            .AsNoTracking()
            .Where(e => episodeIds.Contains(e.Id) && e.SeriesId != null)
            .Select(e => new { e.Id, SeriesId = e.SeriesId!.Value })
            .ToListAsync(cancellationToken);
        if (seriesByEpisode.Count == 0)
        {
            return;
        }

        var seriesIdByEpisode = seriesByEpisode.ToDictionary(e => e.Id, e => e.SeriesId);
        var counts = new Dictionary<Guid, int>();
        foreach (var id in episodeIds)
        {
            if (!seriesIdByEpisode.TryGetValue(id, out var seriesId))
            {
                continue;
            }

            counts[seriesId] = counts.GetValueOrDefault(seriesId) + 1;
        }

        foreach (var (seriesId, count) in counts)
        {
            var key = seriesId.ToString("N");
            if (!anchor.SeriesEpisodeIndex.TryGetValue(key, out var index))
            {
                continue;
            }

            anchor.SeriesEpisodeIndex[key] = Math.Max(0, index - count);
        }
    }

    private static DateTime ResolveSlotPadEnd(DateTime contentEnd, DateTime blockEnd, TimeZoneInfo tz)
    {
        var padUntil = ScheduleTimeZoneHelper.CeilToHalfHourUtc(contentEnd, tz);
        return padUntil > blockEnd ? padUntil : blockEnd;
    }

    private async Task<DateTime?> TryAddOvernightRerunAsync(
        Channel channel,
        DateOnly localDate,
        TimeZoneInfo tz,
        DateTime slotStart,
        DateTime blockEnd,
        List<PlayoutItem> builtPrograms,
        CancellationToken cancellationToken)
    {
        var previousDate = localDate.AddDays(-1);
        var pool = await LoadRerunPoolAsync(channel.Id, previousDate, tz, builtPrograms, cancellationToken);
        if (pool.Count == 0)
        {
            return null;
        }

        var alreadyUsed = builtPrograms
            .Where(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.Start, tz)) == localDate)
            .Select(p => p.JellyfinItemId)
            .ToHashSet();
        var pick = pool.FirstOrDefault(p => !alreadyUsed.Contains(p.JellyfinItemId)) ?? pool[0];
        var duration = pick.OutPoint > TimeSpan.Zero
            ? pick.OutPoint
            : pick.Finish - pick.Start;
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromMinutes(30);
        }

        var contentEnd = slotStart.Add(duration);
        var padUntil = ResolveSlotPadEnd(contentEnd, blockEnd, tz);
        var scheduled = await _commercialService.ScheduleProgramWithBreaksAsync(
            channel,
            new ResolvedCandidate
            {
                JellyfinItemId = pick.JellyfinItemId,
                Title = pick.Title,
                Duration = duration
            },
            slotStart,
            padUntil,
            cancellationToken);
        builtPrograms.AddRange(scheduled.ProgramItems);
        padUntil = ResolveSlotPadEnd(scheduled.TimelineEnd, blockEnd, tz);
        if (padUntil > scheduled.TimelineEnd)
        {
            await _commercialService.PadToSlotAsync(channel, scheduled.TimelineEnd, padUntil, cancellationToken);
        }

        return padUntil;
    }

    private async Task<List<PlayoutItem>> LoadRerunPoolAsync(
        Guid channelId,
        DateOnly localDate,
        TimeZoneInfo tz,
        List<PlayoutItem> builtPrograms,
        CancellationToken cancellationToken)
    {
        var fromBuilt = builtPrograms
            .Where(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.Start, tz)) == localDate)
            .Where(IsProgramRerunSource)
            .ToList();
        if (fromBuilt.Count > 0)
        {
            return RankRerunSources(fromBuilt, tz);
        }

        var startLocal = localDate.ToDateTime(TimeOnly.MinValue);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal.AddDays(1), DateTimeKind.Unspecified), tz);
        var fromDb = await _db.PlayoutItems
            .Where(p => p.ChannelId == channelId && p.Start >= startUtc && p.Start < endUtc)
            .ToListAsync(cancellationToken);

        return RankRerunSources(
            fromDb.Where(p => _db.Entry(p).State != EntityState.Deleted && IsProgramRerunSource(p)).ToList(),
            tz);
    }

    private static bool IsProgramRerunSource(PlayoutItem item)
        => !string.Equals(item.GuideGroup, "commercial", StringComparison.OrdinalIgnoreCase)
            && !item.IsVirtual
            && item.JellyfinItemId.HasValue
            && item.InPoint == TimeSpan.Zero
            && (item.OutPoint > TimeSpan.Zero ? item.OutPoint : item.Finish - item.Start) <= TimeSpan.FromMinutes(45)
            && (item.OutPoint > TimeSpan.Zero ? item.OutPoint : item.Finish - item.Start) >= TimeSpan.FromMinutes(5);

    private static List<PlayoutItem> RankRerunSources(List<PlayoutItem> items, TimeZoneInfo tz)
        => items
            .OrderBy(p => RerunPriority(TimeZoneInfo.ConvertTimeFromUtc(p.Start, tz)))
            .ThenBy(p => p.Start)
            .ToList();

    private static int RerunPriority(DateTime localStart)
    {
        var slot = (localStart.Hour * 60 + localStart.Minute) / 30;
        if (slot is >= 38 and <= 41)
        {
            return 0;
        }

        if (slot is >= 32 and <= 47)
        {
            return 1;
        }

        if (slot >= 16)
        {
            return 2;
        }

        return 3;
    }
}
