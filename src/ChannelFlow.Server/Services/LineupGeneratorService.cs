using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    private readonly StreamService _stream;
    private readonly LogoBumperService _bumpers;
    private readonly OriginalBroadcastSimulator _originalBroadcast;
    private readonly ILogger<LineupGeneratorService> _logger;

    public LineupGeneratorService(
        FinTvDbContext db,
        LineupService lineupService,
        SmartSelectionService smartSelection,
        CommercialService commercialService,
        ChannelService channelService,
        HolidayChannelService holidays,
        GuideUpdateTracker guideUpdates,
        StreamService stream,
        LogoBumperService bumpers,
        OriginalBroadcastSimulator originalBroadcast,
        ILogger<LineupGeneratorService> logger)
    {
        _db = db;
        _lineupService = lineupService;
        _smartSelection = smartSelection;
        _commercialService = commercialService;
        _channelService = channelService;
        _holidays = holidays;
        _guideUpdates = guideUpdates;
        _stream = stream;
        _bumpers = bumpers;
        _originalBroadcast = originalBroadcast;
        _logger = logger;
    }

    public async Task BuildPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode = PlayoutBuildMode.ReplaceWindow,
        CancellationToken cancellationToken = default,
        bool interruptStream = true)
    {
        if (channel.ContentType is ChannelContentType.Weather or ChannelContentType.News)
        {
            await BuildContinuousPlayoutAsync(channel, startUtc, endUtc, mode, cancellationToken);
            NotifyPlayoutChanged(channel, mode, interruptStream);
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
        var toonTakeoverBumperDates = new HashSet<DateOnly>();

        var snapshot = await _lineupService.LoadResolutionSnapshotAsync(channel.Id, cancellationToken);
        _logger.LogInformation(
            "Loaded lineup snapshot for {ChannelName}; filling {Start:u} to {End:u}",
            channel.Name,
            startUtc,
            endUtc);
        var slotsByDate = new Dictionary<DateOnly, IReadOnlyList<LineupSlot>>();
        var anniversaryByDate = new Dictionary<DateOnly, Queue<AnniversaryPick>>();
        var stealEnabled = OriginalBroadcastSimulator.IsEnabled(channel);
        var (primeStart, primeEnd) = stealEnabled
            ? AiPlayoutTemplates.GetPrimetimeSlotRange(channel)
            : (38, 41);
        var cursor = startUtc;
        var steps = 0;
        var maxSteps = Math.Max(96, (int)((endUtc - startUtc).TotalMinutes / 10) + 48);
        while (cursor < endUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++steps > maxSteps)
            {
                _logger.LogError(
                    "Playout build for {ChannelName} stopped after {Steps} steps at {Cursor:u} (end {End:u})",
                    channel.Name,
                    steps,
                    cursor,
                    endUtc);
                break;
            }

            var cursorBefore = cursor;
            var local = TimeZoneInfo.ConvertTimeFromUtc(cursor, tz);
            var date = DateOnly.FromDateTime(local);
            if (!slotsByDate.TryGetValue(date, out var slots))
            {
                slots = _lineupService.ResolveSlotsForDate(snapshot, date);
                slotsByDate[date] = slots;
            }

            var slotIndex = (local.Hour * 60 + local.Minute) / 30;

            if (IsSlotConsumedByEarlierSpan(slots, slotIndex)
                && !(stealEnabled && AiPlayoutTemplates.IsPrimetimeSlot(slotIndex, primeStart, primeEnd)))
            {
                cursor = cursor.AddMinutes(30);
                continue;
            }

            var slot = slots.FirstOrDefault(s => s.SlotIndex == slotIndex);
            if (IsRerunTimeslot(slot, channel, slotIndex))
            {
                var rerunStartLocal = local.Date.AddMinutes(slotIndex * 30);
                var rerunSpan = Math.Clamp(slot?.SpanSlots ?? 1, 1, 8);
                var rerunEndLocal = rerunStartLocal.AddMinutes(30 * rerunSpan);
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
            }

            var stolePrimetime = false;
            if (stealEnabled
                && !IsRerunTimeslot(slot, channel, slotIndex)
                && AiPlayoutTemplates.IsPrimetimeSlot(slotIndex, primeStart, primeEnd))
            {
                if (!anniversaryByDate.TryGetValue(date, out var anniversaryQueue))
                {
                    anniversaryQueue = await _originalBroadcast.BuildQueueAsync(
                        channel,
                        date,
                        slots,
                        cancellationToken).ConfigureAwait(false);
                    anniversaryByDate[date] = anniversaryQueue;
                }

                var steal = OriginalBroadcastSimulator.TryTakeFitting(anniversaryQueue, slotIndex, primeEnd);
                if (steal is AnniversaryPick stolen)
                {
                    slot = OriginalBroadcastSimulator.CreateSlot(slotIndex, stolen);
                    stolePrimetime = true;
                }
            }

            if (slot is null || slot.Candidates.Count == 0)
            {
                if (PastTenseNewsCatalog.IsPastTenseNewsChannel(channel))
                {
                    slot = CreatePastTenseNewsSlot(channel, slotIndex);
                }
                else
                {
                    slot = CreateFilterFallbackSlot(channel, slotIndex);
                }
            }

            var spanSlots = Math.Clamp(slot.SpanSlots, 1, 8);
            var slotStartLocal = local.Date.AddMinutes(slotIndex * 30);
            var blockEndLocal = slotStartLocal.AddMinutes(30 * spanSlots);
            var slotStart = TimeZoneInfo.ConvertTimeToUtc(slotStartLocal, tz);
            var blockEnd = TimeZoneInfo.ConvertTimeToUtc(blockEndLocal, tz);
            if (stolePrimetime)
            {
                var primeEndLocal = DateTime.SpecifyKind(
                    local.Date.AddMinutes((primeEnd + 1) * 30),
                    DateTimeKind.Unspecified);
                var primeEndUtc = TimeZoneInfo.ConvertTimeToUtc(primeEndLocal, tz);
                if (blockEnd > primeEndUtc)
                {
                    blockEnd = primeEndUtc;
                }
            }

            if (blockEnd <= cursor)
            {
                cursor = AdvanceCursorOrSkip(channel.Name, cursorBefore, cursor.AddMinutes(30));
                continue;
            }

            if (slotStart < cursor)
            {
                slotStart = cursor;
            }

            _logger.LogInformation(
                "Playout slot {SlotIndex} for {ChannelName}: {Start:u} to {End:u}",
                slotIndex,
                channel.Name,
                slotStart,
                blockEnd);

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

            if (_bumpers.ShouldOpenToonTakeover(channel, slotIndex) && toonTakeoverBumperDates.Add(date))
            {
                var bumperEnd = await TryAddToonTakeoverBumperAsync(channel, slotStart, cancellationToken);
                if (bumperEnd is DateTime insertedEnd)
                {
                    slotStart = insertedEnd;
                    if (slotStart >= blockEnd)
                    {
                        cursor = slotStart;
                        continue;
                    }
                }
            }

            _logger.LogInformation(
                "Selecting program for {ChannelName} slot {SlotIndex}",
                channel.Name,
                slotIndex);
            var picked = await _smartSelection.PickCandidateAsync(channel, slot, date, anchor, cancellationToken);
            if (picked is null && !slot.Candidates.Any(c => c.Kind == SlotCandidateKind.FilterQuery))
            {
                picked = await _smartSelection.PickCandidateAsync(
                    channel,
                    CreateFilterFallbackSlot(channel, slotIndex),
                    date,
                    anchor,
                    cancellationToken);
            }

            if (picked is null)
            {
                cursor = blockEnd;
                continue;
            }

            if (picked.SeriesId is null && picked.JellyfinItemId is Guid pickedId)
            {
                picked.SeriesId = await _db.Episodes.AsNoTracking()
                    .Where(e => e.Id == pickedId || e.JellyfinItemId == pickedId)
                    .Select(e => e.SeriesId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Selected {Title} ({Duration}) series {SeriesId} for {ChannelName} slot {SlotIndex}",
                picked.Title,
                picked.Duration,
                picked.SeriesId,
                channel.Name,
                slotIndex);

            if (channel.ContentType != ChannelContentType.Music
                && ShortEpisodeBlocks.IsShortRuntime(picked.Duration)
                && picked.SeriesId is Guid shortSeriesId
                && shortSeriesId != Guid.Empty)
            {
                cursor = AdvanceCursorOrSkip(
                    channel.Name,
                    cursorBefore,
                    await PackShortEpisodeBlockAsync(
                        channel,
                        picked,
                        shortSeriesId,
                        date,
                        anchor,
                        slotStart,
                        blockEnd,
                        tz,
                        builtPrograms,
                        cancellationToken));
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

            cursor = AdvanceCursorOrSkip(channel.Name, cursorBefore, padUntil);
        }

        _logger.LogInformation(
            "Finished filling playout window for {ChannelName}: {Steps} slots, cursor {Cursor:u}",
            channel.Name,
            steps,
            cursor);

        await _channelService.SaveAnchorAsync(channel.Id, anchor, cancellationToken);
        await _db.Channels
            .Where(c => c.Id == channel.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.LastPlayoutBuiltAt, DateTime.UtcNow),
                cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        NotifyPlayoutChanged(channel, mode, interruptStream);
    }

    private void NotifyPlayoutChanged(Channel channel, PlayoutBuildMode mode, bool interruptStream = true)
    {
        _guideUpdates.MarkUpdated();
        if (mode != PlayoutBuildMode.ReplaceWindow || !interruptStream)
        {
            return;
        }

        _stream.InterruptCurrentItem(channel.Id);
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

    private async Task<DateTime?> TryAddToonTakeoverBumperAsync(
        Channel channel,
        DateTime start,
        CancellationToken cancellationToken)
    {
        var duration = await _bumpers.TryResolveToonTakeoverDurationAsync(cancellationToken);
        if (duration is not TimeSpan length || length <= TimeSpan.Zero)
        {
            return null;
        }

        var finish = start.Add(length);
        _db.PlayoutItems.Add(new PlayoutItem
        {
            ChannelId = channel.Id,
            Start = start,
            Finish = finish,
            Title = "Slappy's Toon Takeover",
            IsVirtual = true,
            VirtualSource = VirtualContentSource.LogoBumper,
            FillerKind = FillerKind.PreRoll,
            GuideGroup = LogoBumperService.GuideGroup
        });
        return finish;
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

    private static bool IsRerunTimeslot(LineupSlot? slot, Channel channel, int slotIndex)
    {
        if (channel.ContentType != ChannelContentType.TvShow)
        {
            return false;
        }

        if (slot?.IsRerunSlot == true)
        {
            return true;
        }

        return (slot is null || slot.Candidates.Count == 0)
            && NetworkSchedulePlanner.IsOvernightRerunSlot(slotIndex);
    }

    private static LineupSlot CreatePastTenseNewsSlot(Channel channel, int slotIndex)
        => CreateFilterFallbackSlot(channel, slotIndex);

    private static LineupSlot CreateFilterFallbackSlot(Channel channel, int slotIndex)
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

    private async Task<DateTime> PackShortEpisodeBlockAsync(
        Channel channel,
        ResolvedCandidate first,
        Guid seriesId,
        DateOnly date,
        PlayoutAnchorState anchor,
        DateTime slotStart,
        DateTime blockEnd,
        TimeZoneInfo tz,
        List<PlayoutItem> builtPrograms,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Packing shorts for {ChannelName}: {Title} series {SeriesId} from {Start:u} to {End:u}",
            channel.Name,
            first.Title,
            seriesId,
            slotStart,
            blockEnd);

        var seriesEpisodes = await _smartSelection.LoadSeriesShortEpisodesAsync(seriesId, cancellationToken);
        _logger.LogInformation(
            "Loaded {Count} short episodes for {Title} ({SeriesId})",
            seriesEpisodes.Count,
            first.Title,
            seriesId);

        var fillStart = slotStart;
        var packed = 0;
        var usedIds = new HashSet<Guid>();
        var current = first;
        const int maxPacked = 8;
        while (current is not null && fillStart < blockEnd - TimeSpan.FromSeconds(8) && packed < maxPacked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = current.Duration > TimeSpan.Zero ? current.Duration : TimeSpan.FromMinutes(7);
            if (packed > 0 && fillStart + duration > blockEnd)
            {
                break;
            }

            var clipEnd = fillStart + duration;
            if (clipEnd > blockEnd)
            {
                clipEnd = blockEnd;
            }

            var scheduled = await _commercialService.ScheduleProgramWithBreaksAsync(
                channel,
                current,
                fillStart,
                clipEnd,
                cancellationToken);
            builtPrograms.AddRange(scheduled.ProgramItems);
            _db.PlayoutHistory.Add(new PlayoutHistoryEntry
            {
                ChannelId = channel.Id,
                JellyfinItemId = current.JellyfinItemId,
                AiredAt = fillStart,
                Title = current.Title
            });
            if (current.JellyfinItemId is Guid usedId)
            {
                usedIds.Add(usedId);
            }

            var nextStart = scheduled.TimelineEnd > fillStart
                ? scheduled.TimelineEnd
                : fillStart.Add(duration);
            if (nextStart <= fillStart)
            {
                _logger.LogWarning(
                    "Short pack did not advance timeline for {Title} at {Start:u}; stopping pack",
                    current.Title,
                    fillStart);
                fillStart = nextStart;
                packed++;
                break;
            }

            fillStart = nextStart;
            packed++;
            current = _smartSelection.TakeNextShortEpisode(
                seriesEpisodes,
                seriesId,
                date,
                anchor,
                usedIds);
        }

        var padUntil = ResolveSlotPadEnd(fillStart, blockEnd, tz);
        if (padUntil > fillStart)
        {
            await _commercialService.PadToSlotAsync(channel, fillStart, padUntil, cancellationToken);
            fillStart = padUntil;
        }

        _logger.LogInformation(
            "Packed {Packed} shorts for {ChannelName} ({Title}); moving to {Cursor:u}",
            packed,
            channel.Name,
            first.Title,
            fillStart);

        return fillStart;
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
                && !LogoBumperService.IsHiddenFromGuide(p.GuideGroup))
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

    private DateTime AdvanceCursorOrSkip(string channelName, DateTime cursorBefore, DateTime cursor)
    {
        if (cursor > cursorBefore)
        {
            return cursor;
        }

        var forced = cursorBefore.AddMinutes(30);
        _logger.LogWarning(
            "Playout cursor did not advance for {ChannelName} at {Cursor:u}; skipping to {Next:u}",
            channelName,
            cursorBefore,
            forced);
        return forced;
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
        => !LogoBumperService.IsHiddenFromGuide(item.GuideGroup)
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
