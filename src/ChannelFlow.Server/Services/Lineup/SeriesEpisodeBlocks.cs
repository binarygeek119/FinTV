using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// TV series playout: 1–4 episode blocks, a few 4–6 episode mini-marathons per week,
/// and a cooldown so the same show does not fill the day.
/// </summary>
internal static class SeriesEpisodeBlocks
{
    public const int MinNormal = 1;
    public const int MaxNormal = 4;
    public const int MinMarathon = 4;
    public const int MaxMarathon = 6;
    public const int MaxMarathonsPerWeek = 3;
    public const int CooldownShows = 12;

    public static bool AppliesTo(Channel channel)
        => channel.ContentType == ChannelContentType.TvShow
            && !PastTenseNewsCatalog.IsPastTenseNewsChannel(channel)
            && ChannelAiRules.ResolveCatalogMode(channel) != ChannelCatalogMode.MovieOnly;

    public static int WeekKey(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        return System.Globalization.ISOWeek.GetYear(dt) * 100
            + System.Globalization.ISOWeek.GetWeekOfYear(dt);
    }

    public static void EnsureWeek(PlayoutAnchorState anchor, DateOnly date)
    {
        var week = WeekKey(date);
        if (anchor.SeriesBlockWeekKey == week)
        {
            return;
        }

        anchor.SeriesBlockWeekKey = week;
        anchor.MiniMarathonsThisWeek = 0;
        anchor.LastMiniMarathonDayNumber = 0;
    }

    public static int PickNormalLength(Random rng)
    {
        var roll = rng.Next(100);
        if (roll < 15)
        {
            return 1;
        }

        if (roll < 50)
        {
            return 2;
        }

        if (roll < 85)
        {
            return 3;
        }

        return 4;
    }

    public static int PickMarathonLength(Random rng)
        => rng.Next(MinMarathon, MaxMarathon + 1);

    public static bool IsMarathonEligibleSlot(DayOfWeek day, int slotIndex)
    {
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return slotIndex is >= 16 and <= 35;
        }

        return slotIndex is >= 30 and <= 39;
    }

    public static bool ShouldStartMarathon(PlayoutAnchorState anchor, DateOnly date, int slotIndex, int seed)
    {
        EnsureWeek(anchor, date);
        if (anchor.MiniMarathonsThisWeek >= MaxMarathonsPerWeek)
        {
            return false;
        }

        if (anchor.LastMiniMarathonDayNumber == date.DayNumber)
        {
            return false;
        }

        if (!IsMarathonEligibleSlot(date.DayOfWeek, slotIndex))
        {
            return false;
        }

        var dayRoll = Math.Abs(HashCode.Combine(seed, anchor.SeriesBlockWeekKey, (int)date.DayOfWeek)) % 7;
        return dayRoll < MaxMarathonsPerWeek;
    }

    public static HashSet<Guid> CooldownSeries(PlayoutAnchorState anchor, DateOnly date)
    {
        _ = date;
        var set = new HashSet<Guid>();
        var recent = anchor.RecentSeriesIds;
        var take = Math.Min(CooldownShows, recent.Count);
        for (var i = recent.Count - take; i < recent.Count; i++)
        {
            if (recent[i] != Guid.Empty)
            {
                set.Add(recent[i]);
            }
        }

        if (anchor.ActiveSeriesBlockId is Guid active)
        {
            set.Remove(active);
        }

        return set;
    }

    public static void BeginBlock(PlayoutAnchorState anchor, Guid seriesId, int episodeCount, bool marathon, DateOnly date)
    {
        anchor.ActiveSeriesBlockId = seriesId;
        anchor.SeriesBlockRemaining = Math.Max(0, episodeCount - 1);
        if (marathon)
        {
            EnsureWeek(anchor, date);
            anchor.MiniMarathonsThisWeek++;
            anchor.LastMiniMarathonDayNumber = date.DayNumber;
        }

        if (anchor.SeriesBlockRemaining <= 0)
        {
            FinishBlock(anchor, seriesId);
        }
    }

    public static void ContinueOrFinish(PlayoutAnchorState anchor, Guid seriesId)
    {
        if (anchor.SeriesBlockRemaining > 0)
        {
            anchor.SeriesBlockRemaining--;
        }

        if (anchor.SeriesBlockRemaining <= 0 || anchor.ActiveSeriesBlockId != seriesId)
        {
            FinishBlock(anchor, seriesId);
        }
    }

    public static void FinishBlock(PlayoutAnchorState anchor, Guid seriesId)
    {
        anchor.ActiveSeriesBlockId = null;
        anchor.SeriesBlockRemaining = 0;
        if (seriesId == Guid.Empty)
        {
            return;
        }

        anchor.RecentSeriesIds.RemoveAll(id => id == seriesId);
        anchor.RecentSeriesIds.Add(seriesId);
        while (anchor.RecentSeriesIds.Count > CooldownShows)
        {
            anchor.RecentSeriesIds.RemoveAt(0);
        }
    }
}
