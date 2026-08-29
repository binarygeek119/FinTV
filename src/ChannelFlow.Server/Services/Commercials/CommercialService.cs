using ChannelFlow.CommercialDetect;
using FinTv.Configuration;
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
        /// Jellyfin introskip <c>intro</c>, <c>preview</c>, <c>recap</c>, and <c>outro</c> are never mid-roll points.
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
        var rotation = await CreateRotationAsync(channel, start, cancellationToken);

        if (preCount > 0)
        {
            cursor = await AddCommercialBreakAsync(
                channel,
                cursor,
                preCount,
                FillerKind.PreRoll,
                cancellationToken,
                rotation);
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
                    cancellationToken,
                    rotation);
            }
        }

        var remaining = duration - segmentStart;
        if (remaining > TimeSpan.FromSeconds(1))
        {
            programs.Add(AddProgramSegment(channel, content, cursor, segmentStart, remaining, duration));
            cursor = cursor.Add(remaining);
        }

        var padUntil = preferredSlotEnd > cursor ? preferredSlotEnd : cursor;
        await FillSlotPaddingAsync(channel, preset, cursor, padUntil, cancellationToken, rotation);
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
        CancellationToken cancellationToken,
        CommercialRotationState? rotation = null)
    {
        if (slotEnd is not DateTime until || until <= from)
        {
            return;
        }

        _ = preset;
        rotation ??= await CreateRotationAsync(channel, from, cancellationToken);
        if (rotation.Buckets.Count == 0)
        {
            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                Start = from,
                Finish = until,
                Title = "Filler",
                FillerKind = FillerKind.PostRoll,
                GuideGroup = "commercial",
                IsVirtual = true
            });
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

            var pick = PickNextCommercial(rotation, remaining)
                ?? PickNextCommercial(rotation, maxDuration: null);
            if (pick is null || pick.Duration <= TimeSpan.Zero)
            {
                break;
            }

            var end = pick.Duration <= remaining ? cursor.Add(pick.Duration) : until;
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
        var rotation = await CreateRotationAsync(channel, DateTime.UtcNow, cancellationToken);
        var picks = new List<Commercial>();
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            var pick = PickNextCommercial(rotation, maxDuration: null);
            if (pick is null)
            {
                break;
            }

            picks.Add(pick);
        }

        return picks;
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
        CancellationToken cancellationToken,
        CommercialRotationState? rotation = null)
    {
        rotation ??= await CreateRotationAsync(channel, start, cancellationToken);
        var cursor = start;
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            var commercial = PickNextCommercial(rotation, maxDuration: null);
            if (commercial is null || commercial.Duration <= TimeSpan.Zero)
            {
                break;
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

    private async Task<CommercialRotationState> CreateRotationAsync(
        Channel channel,
        DateTime before,
        CancellationToken cancellationToken)
    {
        var all = await LoadChannelCommercialsAsync(channel, cancellationToken);
        var buckets = BuildBuckets(channel, all);
        var state = new CommercialRotationState { Buckets = buckets };
        await SeedRotationAsync(channel, before, state, cancellationToken);
        return state;
    }

    private async Task<List<Commercial>> LoadChannelCommercialsAsync(Channel channel, CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var playlistIds = channel.CommercialSearchPlaylistIds;
        var query = _db.Commercials.AsNoTracking().AsQueryable();
        HashSet<string>? playlistSbids = null;

        if (playlistIds.Count > 0)
        {
            playlistSbids = (config?.CommercialSearchPlaylists ?? [])
                .Where(playlist => playlistIds.Contains(playlist.Id))
                .SelectMany(playlist => playlist.VideoSbids ?? [])
                .Where(sbid => !string.IsNullOrWhiteSpace(sbid))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (playlistSbids.Count == 0)
            {
                return [];
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

        return all;
    }

    private static List<CommercialBucket> BuildBuckets(Channel channel, IReadOnlyList<Commercial> all)
    {
        if (all.Count == 0)
        {
            return [];
        }

        var playlists = (FinTvRuntime.Current?.Configuration.CommercialSearchPlaylists ?? [])
            .Where(playlist => channel.CommercialSearchPlaylistIds.Contains(playlist.Id))
            .ToList();
        if (playlists.Count > 0)
        {
            var buckets = new List<CommercialBucket>();
            foreach (var playlist in playlists)
            {
                var sbids = (playlist.VideoSbids ?? [])
                    .Where(sbid => !string.IsNullOrWhiteSpace(sbid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var items = all
                    .Where(c => c.CommercialBrainzVideoSbid != null && sbids.Contains(c.CommercialBrainzVideoSbid))
                    .ToList();
                if (items.Count == 0)
                {
                    continue;
                }

                buckets.Add(new CommercialBucket(playlist.Id.ToString("N"), playlist.Name, items));
            }

            if (buckets.Count > 1)
            {
                return buckets;
            }

            if (buckets.Count == 1)
            {
                all = buckets[0].Items;
            }
        }

        return all
            .GroupBy(FallbackTypeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CommercialBucket(group.Key, group.Key, group.ToList()))
            .Where(bucket => bucket.Items.Count > 0)
            .ToList();
    }

    private async Task SeedRotationAsync(
        Channel channel,
        DateTime before,
        CommercialRotationState state,
        CancellationToken cancellationToken)
    {
        var pending = _db.ChangeTracker.Entries<PlayoutItem>()
            .Select(entry => entry.Entity)
            .Where(item => item.ChannelId == channel.Id && item.CommercialId.HasValue && item.Start < before)
            .OrderBy(item => item.Start)
            .Select(item => item.CommercialId!.Value)
            .ToList();
        var recent = await _db.PlayoutItems
            .AsNoTracking()
            .Where(item => item.ChannelId == channel.Id && item.CommercialId != null && item.Start < before)
            .OrderByDescending(item => item.Start)
            .Take(24)
            .Select(item => item.CommercialId!.Value)
            .ToListAsync(cancellationToken);
        recent.Reverse();

        var history = pending.Count > 0 ? pending : recent;
        var byId = state.Buckets
            .SelectMany(bucket => bucket.Items.Select(item => (item.Id, bucket.TypeKey)))
            .GroupBy(pair => pair.Id)
            .ToDictionary(group => group.Key, group => group.First().TypeKey);
        foreach (var commercialId in history)
        {
            if (!byId.TryGetValue(commercialId, out var typeKey))
            {
                continue;
            }

            state.LastTypeKey = typeKey;
            state.LastIndexByType[typeKey] = state.PickCount;
            state.PickCount++;
            RememberRecent(state, commercialId);
        }
    }

    private static Commercial? PickNextCommercial(CommercialRotationState state, TimeSpan? maxDuration)
    {
        if (state.Buckets.Count == 0)
        {
            return null;
        }

        var ranked = new List<(CommercialBucket Bucket, List<Commercial> Fitting, int LastAt)>();
        foreach (var bucket in state.Buckets)
        {
            var fitting = bucket.Items
                .Where(item => item.Duration > TimeSpan.Zero
                    && (maxDuration is null || item.Duration <= maxDuration.Value))
                .ToList();
            if (fitting.Count == 0)
            {
                continue;
            }

            ranked.Add((
                bucket,
                fitting,
                state.LastIndexByType.GetValueOrDefault(bucket.TypeKey, int.MinValue / 4)));
        }

        if (ranked.Count == 0)
        {
            return null;
        }

        var withoutLast = ranked
            .Where(row => !string.Equals(row.Bucket.TypeKey, state.LastTypeKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (withoutLast.Count > 0)
        {
            ranked = withoutLast;
        }

        var farthest = ranked.Min(row => row.LastAt);
        var candidates = ranked.Where(row => row.LastAt == farthest).ToList();
        var chosen = candidates[Random.Shared.Next(candidates.Count)];
        var unused = chosen.Fitting.Where(item => !state.RecentIds.Contains(item.Id)).ToList();
        var pool = unused.Count > 0 ? unused : chosen.Fitting;
        var pick = pool[Random.Shared.Next(pool.Count)];

        state.LastTypeKey = chosen.Bucket.TypeKey;
        state.LastIndexByType[chosen.Bucket.TypeKey] = state.PickCount;
        state.PickCount++;
        RememberRecent(state, pick.Id);
        return pick;
    }

    private static void RememberRecent(CommercialRotationState state, Guid commercialId)
    {
        if (!state.RecentIds.Add(commercialId))
        {
            return;
        }

        state.RecentOrder.Enqueue(commercialId);
        while (state.RecentOrder.Count > 12)
        {
            var oldest = state.RecentOrder.Dequeue();
            state.RecentIds.Remove(oldest);
        }
    }

    private static string FallbackTypeKey(Commercial commercial)
    {
        if (!string.IsNullOrWhiteSpace(commercial.Brand))
        {
            return "brand:" + commercial.Brand.Trim().ToLowerInvariant();
        }

        if (commercial.Decade is int decade)
        {
            return "decade:" + decade;
        }

        return "source:" + commercial.Source;
    }

    private sealed class CommercialRotationState
    {
        public List<CommercialBucket> Buckets { get; init; } = [];

        public string? LastTypeKey { get; set; }

        public Dictionary<string, int> LastIndexByType { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Queue<Guid> RecentOrder { get; } = new();

        public HashSet<Guid> RecentIds { get; } = [];

        public int PickCount { get; set; }
    }

    private sealed record CommercialBucket(string TypeKey, string Label, List<Commercial> Items);

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
                .Where(chapter => !IntroSkipLayout.IsOpeningOrOutroName(chapter.Name))
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
