using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Rugrats / Looney Tunes style shorts: pack into one timeslot and list as one named guide block.
/// </summary>
internal static class ShortEpisodeBlocks
{
    public static readonly TimeSpan MaxEpisodeDuration = TimeSpan.FromMinutes(18);

    public static readonly TimeSpan MaxGuideMergeGap = TimeSpan.FromMinutes(12);

    public static bool IsShortRuntime(TimeSpan duration)
        => duration > TimeSpan.Zero && duration < MaxEpisodeDuration;

    public static bool TryGetShortEpisode(
        DateTime start,
        DateTime finish,
        Guid? jellyfinItemId,
        IReadOnlyDictionary<Guid, ShortEpisodeCatalogInfo> episodes,
        out ShortEpisodeCatalogInfo info)
    {
        info = default;
        if (jellyfinItemId is not Guid id || !episodes.TryGetValue(id, out info))
        {
            return false;
        }

        if (info.SeriesId == Guid.Empty)
        {
            return false;
        }

        var played = finish - start;
        var duration = played > TimeSpan.Zero ? played : info.Runtime ?? TimeSpan.Zero;
        if (info.Runtime is TimeSpan catalog && catalog > TimeSpan.Zero && catalog < duration)
        {
            duration = catalog;
        }

        return IsShortRuntime(duration);
    }

    /// <summary>
    /// Consecutive shorts of the same series (with at most <see cref="MaxGuideMergeGap"/> between them)
    /// that packed into a run of two or more episodes.
    /// </summary>
    public static HashSet<Guid> FindPackedShortPlayoutIds(
        IReadOnlyList<PlayoutItem> programs,
        IReadOnlyDictionary<Guid, ShortEpisodeCatalogInfo> episodes)
    {
        var packed = new HashSet<Guid>();
        var ordered = programs.OrderBy(item => item.Start).ToList();
        var i = 0;
        while (i < ordered.Count)
        {
            if (!TryGetShortEpisode(ordered[i].Start, ordered[i].Finish, ordered[i].JellyfinItemId, episodes, out var first))
            {
                i++;
                continue;
            }

            var run = new List<PlayoutItem> { ordered[i] };
            var j = i + 1;
            while (j < ordered.Count
                && TryGetShortEpisode(ordered[j].Start, ordered[j].Finish, ordered[j].JellyfinItemId, episodes, out var next)
                && next.SeriesId == first.SeriesId
                && ordered[j].Start <= run[^1].Finish.Add(MaxGuideMergeGap))
            {
                run.Add(ordered[j]);
                j++;
            }

            if (run.Count >= 2)
            {
                foreach (var item in run)
                {
                    packed.Add(item.Id);
                }
            }

            i = j;
        }

        return packed;
    }
}

internal readonly record struct ShortEpisodeCatalogInfo(Guid SeriesId, TimeSpan? Runtime);
