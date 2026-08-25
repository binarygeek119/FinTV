using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public class CommercialService
{
    private readonly FinTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinCatalogService _catalog;

    private readonly IChapterManager _chapterManager;

    public CommercialService(FinTvDbContext db, ILibraryManager libraryManager, JellyfinCatalogService catalog, IChapterManager chapterManager)
    {
        _db = db;
        _libraryManager = libraryManager;
        _catalog = catalog;
        _chapterManager = chapterManager;
    }

    public async Task InsertCommercialsAsync(
        Channel channel,
        ResolvedCandidate content,
        DateTime contentStart,
        DateTime contentEnd,
        CancellationToken cancellationToken,
        DateTime? slotEnd = null)
    {
        await FillSlotPaddingAsync(channel, preset: null, contentEnd, slotEnd, cancellationToken);
        _ = content;
        _ = contentStart;
    }

    public Task PadToSlotAsync(
        Channel channel,
        DateTime from,
        DateTime until,
        CancellationToken cancellationToken)
        => FillSlotPaddingAsync(channel, preset: null, from, until, cancellationToken);

    /// <summary>
    /// Lays out a show/movie with start commercials, chapter mid-breaks, and end-of-slot padding.
    /// Mid-breaks take clock time (the program is split at chapters) so they help fill the timeslot.
    /// </summary>
    public async Task<ScheduledProgramResult> ScheduleProgramWithBreaksAsync(
        Channel channel,
        ResolvedCandidate content,
        DateTime start,
        DateTime preferredSlotEnd,
        CancellationToken cancellationToken)
    {
        var preset = await ResolvePresetAsync(channel, cancellationToken);
        var duration = content.Duration > TimeSpan.Zero
            ? content.Duration
            : (preferredSlotEnd > start ? preferredSlotEnd - start : TimeSpan.FromMinutes(30));
        var midBreaks = GetMidBreakOffsets(content.JellyfinItemId, preset, duration);
        var preCount = preset is null
            ? 0
            : (preset.PreRollCount > 0 ? preset.PreRollCount : Math.Max(1, preset.PostRollCount));
        var midCount = preset is null
            ? 0
            : (preset.MidRollCount > 0 ? preset.MidRollCount : Math.Max(1, preset.PostRollCount));

        var cursor = start;
        var programs = new List<PlayoutItem>();

        if (preCount > 0)
        {
            cursor = await AddCommercialBreakAsync(
                channel,
                cursor,
                preCount,
                FillerKind.PreRoll,
                cancellationToken);
        }

        var segmentStart = TimeSpan.Zero;
        foreach (var breakOffset in midBreaks)
        {
            if (breakOffset <= segmentStart || breakOffset >= duration)
            {
                continue;
            }

            var length = breakOffset - segmentStart;
            programs.Add(AddProgramSegment(channel, content, cursor, segmentStart, length, duration));
            cursor = cursor.Add(length);
            segmentStart = breakOffset;

            if (midCount > 0)
            {
                cursor = await AddCommercialBreakAsync(
                    channel,
                    cursor,
                    midCount,
                    FillerKind.MidRoll,
                    cancellationToken);
            }
        }

        var remaining = duration - segmentStart;
        if (remaining > TimeSpan.FromSeconds(1))
        {
            programs.Add(AddProgramSegment(channel, content, cursor, segmentStart, remaining, duration));
            cursor = cursor.Add(remaining);
        }

        var padUntil = preferredSlotEnd > cursor ? preferredSlotEnd : cursor;
        await FillSlotPaddingAsync(channel, preset, cursor, padUntil, cancellationToken);
        if (padUntil > cursor)
        {
            cursor = padUntil;
        }

        return new ScheduledProgramResult
        {
            TimelineEnd = cursor,
            ProgramItems = programs
        };
    }

    private async Task<CommercialPreset?> ResolvePresetAsync(Channel channel, CancellationToken cancellationToken)
    {
        CommercialPreset? preset = null;
        if (channel.CommercialPresetId is Guid presetId)
        {
            preset = await _db.CommercialPresets
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == presetId, cancellationToken);
        }

        return preset
            ?? await _db.CommercialPresets.AsNoTracking().OrderBy(p => p.Name).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task FillSlotPaddingAsync(
        Channel channel,
        CommercialPreset? preset,
        DateTime from,
        DateTime? slotEnd,
        CancellationToken cancellationToken)
    {
        if (slotEnd is not DateTime until || until <= from)
        {
            return;
        }

        preset ??= await ResolvePresetAsync(channel, cancellationToken);
        if (preset is null)
        {
            return;
        }

        var pool = await PickCommercialsAsync(channel, 32, cancellationToken);
        if (pool.Count == 0)
        {
            return;
        }

        var cursor = from;
        var guard = 0;
        while (cursor < until && guard++ < 64)
        {
            var remaining = until - cursor;
            if (remaining < TimeSpan.FromSeconds(3))
            {
                break;
            }

            var fitting = pool
                .Where(commercial => commercial.Duration > TimeSpan.Zero && commercial.Duration <= remaining)
                .ToList();
            Commercial pick;
            DateTime end;
            if (fitting.Count > 0)
            {
                pick = fitting[Random.Shared.Next(fitting.Count)];
                end = cursor.Add(pick.Duration);
            }
            else
            {
                pick = pool
                    .Where(commercial => commercial.Duration > TimeSpan.Zero)
                    .OrderBy(commercial => commercial.Duration)
                    .FirstOrDefault()
                    ?? pool[0];
                if (pick.Duration <= TimeSpan.Zero)
                {
                    break;
                }

                end = until;
            }

            AddCommercialPlayoutItem(channel.Id, pick, cursor, end, FillerKind.PostRoll);
            cursor = end;
        }
    }

    private void AddCommercialPlayoutItem(
        Guid channelId,
        Commercial commercial,
        DateTime start,
        DateTime end,
        FillerKind fillerKind)
    {
        _db.PlayoutItems.Add(new PlayoutItem
        {
            ChannelId = channelId,
            CommercialId = commercial.Id,
            JellyfinItemId = commercial.Source == CommercialSource.Jellyfin && commercial.JellyfinItemId != Guid.Empty
                ? commercial.JellyfinItemId
                : null,
            Start = start,
            Finish = end,
            Title = commercial.Title,
            FillerKind = fillerKind,
            GuideGroup = "commercial"
        });
    }

    public async Task<List<Commercial>> PickCommercialsAsync(Channel channel, int count, CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var playlistIds = channel.CommercialSearchPlaylistIds;
        var query = _db.Commercials.AsNoTracking().AsQueryable();
        HashSet<string>? playlistSbids = null;

        if (playlistIds.Count > 0)
        {
            playlistSbids = (config?.CommercialSearchPlaylists ?? new List<Configuration.CommercialSearchPlaylist>())
                .Where(playlist => playlistIds.Contains(playlist.Id))
                .SelectMany(playlist => playlist.VideoSbids ?? new List<string>())
                .Where(sbid => !string.IsNullOrWhiteSpace(sbid))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (playlistSbids.Count == 0)
            {
                return new List<Commercial>();
            }

            query = query.Where(c =>
                c.Source == CommercialSource.CommercialBrainz
                && c.CommercialBrainzVideoSbid != null);
        }
        else
        {
            var poolMode = config?.CommercialBrainz?.PoolMode ?? CommercialPoolMode.Both;
            query = poolMode switch
            {
                CommercialPoolMode.JellyfinOnly => query.Where(c => c.Source == CommercialSource.Jellyfin),
                CommercialPoolMode.CommercialBrainzOnly => query.Where(c => c.Source == CommercialSource.CommercialBrainz),
                _ => query
            };
        }

        var all = await query.ToListAsync(cancellationToken);
        if (playlistSbids is not null)
        {
            all = all
                .Where(c => c.CommercialBrainzVideoSbid != null && playlistSbids.Contains(c.CommercialBrainzVideoSbid))
                .ToList();
        }
        if (all.Count == 0)
        {
            return new List<Commercial>();
        }

        var rng = Random.Shared;
        return Enumerable.Range(0, count)
            .Select(_ => all[rng.Next(all.Count)])
            .ToList();
    }

    public async Task SyncCommercialLibraryAsync(CancellationToken cancellationToken = default)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "channelflow-commercial",
            "fintv-commercial"
        };
        if (!string.IsNullOrWhiteSpace(config?.CommercialLibraryTag))
        {
            tags.Add(config.CommercialLibraryTag);
        }

        var items = tags
            .SelectMany(tag => _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                Recursive = true,
                Tags = new[] { tag }
            }).Items)
            .DistinctBy(item => item.Id)
            .ToList();
        foreach (var item in items)
        {
            var existing = await _db.Commercials
                .FirstOrDefaultAsync(
                    c => c.Source == CommercialSource.Jellyfin && c.JellyfinItemId == item.Id,
                    cancellationToken);
            var duration = _catalog.GetRuntime(item);
            if (existing is null)
            {
                _db.Commercials.Add(new Commercial
                {
                    Source = CommercialSource.Jellyfin,
                    JellyfinItemId = item.Id,
                    Title = item.Name,
                    Duration = duration
                });
            }
            else
            {
                existing.Title = item.Name;
                existing.Duration = duration;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<Commercial>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Commercials
            .Include(c => c.Chapters)
            .AsNoTracking()
            .OrderBy(c => c.Title)
            .ToListAsync(cancellationToken);
    }

    private async Task<DateTime> AddCommercialBreakAsync(
        Channel channel,
        DateTime start,
        int count,
        FillerKind fillerKind,
        CancellationToken cancellationToken)
    {
        var commercials = await PickCommercialsAsync(channel, Math.Max(1, count), cancellationToken);
        var cursor = start;
        foreach (var commercial in commercials)
        {
            if (commercial.Duration <= TimeSpan.Zero)
            {
                continue;
            }

            var end = cursor.Add(commercial.Duration);
            AddCommercialPlayoutItem(channel.Id, commercial, cursor, end, fillerKind);
            cursor = end;
        }

        return cursor;
    }

    /// <summary>
    /// Splits the item now on air so a mid-roll commercial break starts after <paramref name="delay"/>.
    /// The remainder of the program resumes after the spots.
    /// </summary>
    public async Task<ForcedCommercialBreakResult> ForceCommercialBreakAsync(
        Channel channel,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (channel.ContentType is ChannelContentType.Weather or ChannelContentType.News)
        {
            return ForcedCommercialBreakResult.Skipped(channel.Name, "Weather and news channels do not play commercial breaks.");
        }

        var now = DateTime.UtcNow;
        var current = await _db.PlayoutItems
            .Where(item => item.ChannelId == channel.Id && item.Start <= now && item.Finish > now)
            .OrderByDescending(item => item.Start)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            return ForcedCommercialBreakResult.Skipped(channel.Name, "Nothing is scheduled on this channel right now.");
        }

        if (current.CommercialId.HasValue)
        {
            return ForcedCommercialBreakResult.Skipped(channel.Name, "Already in a commercial.");
        }

        var breakAt = now.Add(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay);
        if (breakAt >= current.Finish)
        {
            var next = await _db.PlayoutItems
                .Where(item => item.ChannelId == channel.Id && item.Id != current.Id && item.Start >= current.Finish.AddSeconds(-0.5))
                .OrderBy(item => item.Start)
                .FirstOrDefaultAsync(cancellationToken);
            if (next?.CommercialId is not null)
            {
                return ForcedCommercialBreakResult.Skipped(channel.Name, "A commercial break is already next.");
            }

            breakAt = current.Finish;
        }

        var preset = await ResolvePresetAsync(channel, cancellationToken);
        var spotCount = preset is { MidRollCount: > 0 }
            ? preset.MidRollCount
            : Math.Max(2, preset?.PostRollCount ?? 2);
        var pool = await PickCommercialsAsync(channel, spotCount, cancellationToken);
        if (pool.Count == 0 || pool.All(commercial => commercial.Duration <= TimeSpan.Zero))
        {
            return ForcedCommercialBreakResult.Skipped(channel.Name, "No commercials are available for this channel.");
        }

        var originalFinish = current.Finish;
        var remaining = originalFinish - breakAt;
        var mediaInAtBreak = current.InPoint + (breakAt - current.Start);
        var later = await _db.PlayoutItems
            .Where(item => item.ChannelId == channel.Id && item.Id != current.Id && item.Start >= originalFinish)
            .ToListAsync(cancellationToken);

        current.Finish = breakAt;
        var commercialEnd = await AddCommercialBreakAsync(
            channel,
            breakAt,
            spotCount,
            FillerKind.MidRoll,
            cancellationToken);
        if (commercialEnd <= breakAt)
        {
            current.Finish = originalFinish;
            return ForcedCommercialBreakResult.Skipped(channel.Name, "No commercials are available for this channel.");
        }

        if (remaining > TimeSpan.FromSeconds(1))
        {
            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                JellyfinItemId = current.JellyfinItemId,
                Start = commercialEnd,
                Finish = commercialEnd + remaining,
                InPoint = mediaInAtBreak,
                OutPoint = current.OutPoint,
                Title = current.Title,
                FillerKind = FillerKind.None,
                GuideGroup = current.GuideGroup,
                IsVirtual = current.IsVirtual,
                VirtualSource = current.VirtualSource
            });
        }

        var shift = commercialEnd - breakAt;
        foreach (var item in later)
        {
            item.Start += shift;
            item.Finish += shift;
        }

        await _db.SaveChangesAsync(cancellationToken);
        var delaySeconds = Math.Max(1, (int)Math.Round((breakAt - now).TotalSeconds));
        return ForcedCommercialBreakResult.Ok(channel.Name, delaySeconds);
    }

    private PlayoutItem AddProgramSegment(
        Channel channel,
        ResolvedCandidate content,
        DateTime start,
        TimeSpan inPoint,
        TimeSpan length,
        TimeSpan fullDuration)
    {
        var item = new PlayoutItem
        {
            ChannelId = channel.Id,
            JellyfinItemId = content.JellyfinItemId,
            Start = start,
            Finish = start.Add(length),
            InPoint = inPoint,
            OutPoint = fullDuration,
            Title = content.Title,
            IsVirtual = content.IsVirtual,
            VirtualSource = content.VirtualSource
        };
        _db.PlayoutItems.Add(item);
        return item;
    }

    private List<TimeSpan> GetMidBreakOffsets(Guid? jellyfinItemId, CommercialPreset? preset, TimeSpan duration)
    {
        if (preset is null || duration < TimeSpan.FromMinutes(8))
        {
            return [];
        }

        var offsets = new List<TimeSpan>();
        var minLead = TimeSpan.FromSeconds(25);
        var minTail = TimeSpan.FromSeconds(25);
        var minGap = TimeSpan.FromMinutes(4);

        if (preset.BreakMode is CommercialBreakMode.ChaptersThenTimer or CommercialBreakMode.ChaptersOnly
            && jellyfinItemId is Guid itemId)
        {
            var chapters = _chapterManager.GetChapters(itemId)
                .OrderBy(chapter => chapter.StartPositionTicks)
                .ToList();
            var namedCommercials = chapters
                .Where(chapter => IsCommercialChapterName(chapter.Name))
                .ToList();
            var source = namedCommercials.Count > 0 ? namedCommercials : chapters;

            foreach (var chapter in source)
            {
                var offset = TimeSpan.FromTicks(Math.Max(0, chapter.StartPositionTicks));
                if (offset < minLead || offset > duration - minTail)
                {
                    continue;
                }

                if (offsets.Count > 0 && offset - offsets[^1] < minGap)
                {
                    continue;
                }

                offsets.Add(offset);
                if (offsets.Count >= 8)
                {
                    break;
                }
            }
        }

        if (offsets.Count == 0 && preset.BreakMode is CommercialBreakMode.ChaptersThenTimer or CommercialBreakMode.TimerOnly)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, preset.TimerIntervalMinutes));
            var cursor = interval;
            while (cursor < duration - minTail && offsets.Count < 8)
            {
                if (cursor >= minLead)
                {
                    offsets.Add(cursor);
                }

                cursor += interval;
            }
        }

        return offsets;
    }

    private static bool IsCommercialChapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("commercial", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mid-break", StringComparison.OrdinalIgnoreCase)
            || name.Contains("midbreak", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ad break", StringComparison.OrdinalIgnoreCase)
            || (name.Contains("break", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("breakfast", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ScheduledProgramResult
{
    public DateTime TimelineEnd { get; init; }

    public List<PlayoutItem> ProgramItems { get; init; } = [];
}

public sealed class ForcedCommercialBreakResult
{
    public bool Forced { get; init; }

    public string ChannelName { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public int DelaySeconds { get; init; }

    public static ForcedCommercialBreakResult Ok(string channelName, int delaySeconds)
        => new()
        {
            Forced = true,
            ChannelName = channelName,
            DelaySeconds = delaySeconds,
            Message = $"Goes to commercial in {delaySeconds} seconds."
        };

    public static ForcedCommercialBreakResult Skipped(string channelName, string message)
        => new()
        {
            Forced = false,
            ChannelName = channelName,
            Message = message
        };
}
