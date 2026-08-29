using System.Text.Json;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Pages the live catalog to the LLM and stores per-channel AI pools.
/// </summary>
public sealed class ChannelCatalogPoolService
{
    public const int PageSize = 90;

    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;

    private readonly FinTvDbContext _db;
    private readonly LlmClientService _llm;
    private readonly HolidayChannelService _holidays;
    private readonly ILogger<ChannelCatalogPoolService> _logger;

    public ChannelCatalogPoolService(
        FinTvDbContext db,
        LlmClientService llm,
        HolidayChannelService holidays,
        ILogger<ChannelCatalogPoolService> logger)
    {
        _db = db;
        _llm = llm;
        _holidays = holidays;
        _logger = logger;
    }

    public async Task<int> CountAsync(Guid channelId, CancellationToken cancellationToken)
        => await _db.ChannelCatalogPool.CountAsync(row => row.ChannelId == channelId, cancellationToken);

    public async Task ClearAsync(Guid channelId, CancellationToken cancellationToken)
    {
        await _db.ChannelCatalogPool.Where(row => row.ChannelId == channelId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await _db.ChannelCatalogPool.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> PruneMissingAsync(CancellationToken cancellationToken)
    {
        var liveShows = _db.TvShows.Where(row => !row.IsMissing).Select(row => row.Id);
        var liveMovies = _db.Movies.Where(row => !row.IsMissing).Select(row => row.Id);
        var liveClips = _db.PastTenseNews.Where(row => !row.IsMissing).Select(row => row.Id);
        return await _db.ChannelCatalogPool
            .Where(row => !liveShows.Contains(row.JellyfinItemId)
                && !liveMovies.Contains(row.JellyfinItemId)
                && !liveClips.Contains(row.JellyfinItemId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task EnsurePrimedAsync(
        Channel channel,
        IReadOnlyList<Channel>? batchChannels,
        Action<string, int, int>? onPage,
        CancellationToken cancellationToken)
    {
        if (channel.ContentType == ChannelContentType.MusicVideo)
        {
            return;
        }

        await PruneMissingAsync(cancellationToken);
        if (await CountAsync(channel.Id, cancellationToken) > 0)
        {
            return;
        }

        if (batchChannels is { Count: > 1 })
        {
            return;
        }

        await PrimeSingleChannelAsync(channel, onPage, cancellationToken);
    }

    public async Task PrimeEmptyChannelsAsync(
        IReadOnlyList<Channel> channels,
        Action<string, int, int>? onPage,
        CancellationToken cancellationToken)
    {
        var needPrime = new List<Channel>();
        foreach (var channel in channels)
        {
            if (channel.ContentType == ChannelContentType.MusicVideo)
            {
                continue;
            }

            if (await CountAsync(channel.Id, cancellationToken) == 0)
            {
                needPrime.Add(channel);
            }
        }

        if (needPrime.Count == 0)
        {
            return;
        }

        if (needPrime.Count == 1)
        {
            await PrimeSingleChannelAsync(needPrime[0], onPage, cancellationToken);
            return;
        }

        await PrimeSharedAsync(needPrime, onPage, cancellationToken);
    }

    public async Task<List<AiCatalogEntry>> LoadPoolEntriesAsync(Channel channel, CancellationToken cancellationToken)
    {
        await PruneMissingAsync(cancellationToken);
        var ids = await _db.ChannelCatalogPool.AsNoTracking()
            .Where(row => row.ChannelId == channel.Id)
            .Select(row => new { row.JellyfinItemId, row.Kind })
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        var idSet = ids.Select(row => row.JellyfinItemId).ToHashSet();
        var firstYears = await LoadFirstEpisodeYearsAsync(idSet, cancellationToken);
        var entries = new List<AiCatalogEntry>();
        var shows = await _db.TvShows.AsNoTracking()
            .Where(row => idSet.Contains(row.Id) && !row.IsMissing)
            .ToListAsync(cancellationToken);
        foreach (var show in shows)
        {
            entries.Add(MapShow(show, firstYears));
        }

        var movies = await _db.Movies.AsNoTracking()
            .Where(row => idSet.Contains(row.Id) && !row.IsMissing)
            .ToListAsync(cancellationToken);
        foreach (var movie in movies)
        {
            entries.Add(MapMovie(movie));
        }

        var clips = await _db.PastTenseNews.AsNoTracking()
            .Where(row => idSet.Contains(row.Id) && !row.IsMissing)
            .ToListAsync(cancellationToken);
        foreach (var clip in clips)
        {
            entries.Add(MapClip(clip));
        }

        return entries
            .OrderBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task PrimeSingleChannelAsync(
        Channel channel,
        Action<string, int, int>? onPage,
        CancellationToken cancellationToken)
    {
        var catalog = await LoadEligibleCatalogAsync(channel, cancellationToken);
        if (catalog.Count == 0)
        {
            throw new InvalidOperationException($"No live catalog titles match {channel.Name}.");
        }

        var pages = Paginate(catalog, PageSize);
        var picked = new HashSet<Guid>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onPage?.Invoke(channel.Name, i + 1, pages.Count);
            var page = pages[i];
            var ids = await AskPicksAsync(channel, page, cancellationToken);
            foreach (var id in ids)
            {
                picked.Add(id);
            }
        }

        await SavePicksAsync(channel.Id, catalog.Where(e => picked.Contains(e.Id)).ToList(), cancellationToken);
        _logger.LogInformation("AI pool for {Channel}: {Picked}/{Available} titles", channel.Name, picked.Count, catalog.Count);
        if (picked.Count == 0)
        {
            throw new InvalidOperationException($"The AI did not pick any titles for {channel.Name}.");
        }
    }

    private async Task PrimeSharedAsync(
        IReadOnlyList<Channel> channels,
        Action<string, int, int>? onPage,
        CancellationToken cancellationToken)
    {
        var catalog = await LoadAllLiveCatalogAsync(cancellationToken);
        if (catalog.Count == 0)
        {
            throw new InvalidOperationException("No live TV shows or movies are in the catalog.");
        }

        var pages = Paginate(catalog, PageSize);
        var picks = channels.ToDictionary(c => c.Id, _ => new HashSet<Guid>());
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onPage?.Invoke("All channels", i + 1, pages.Count);
            var page = pages[i];
            var assigned = await AskAssignmentsAsync(channels, page, cancellationToken);
            foreach (var (channelId, ids) in assigned)
            {
                if (!picks.TryGetValue(channelId, out var set))
                {
                    continue;
                }

                foreach (var id in ids)
                {
                    set.Add(id);
                }
            }
        }

        foreach (var channel in channels)
        {
            var allowed = (await LoadEligibleCatalogAsync(channel, cancellationToken))
                .Select(e => e.Id)
                .ToHashSet();
            var chosen = catalog.Where(e => picks[channel.Id].Contains(e.Id) && allowed.Contains(e.Id)).ToList();
            await SavePicksAsync(channel.Id, chosen, cancellationToken);
            _logger.LogInformation("AI pool for {Channel}: {Picked} titles", channel.Name, chosen.Count);
        }
    }

    private async Task SavePicksAsync(Guid channelId, IReadOnlyList<AiCatalogEntry> picks, CancellationToken cancellationToken)
    {
        await _db.ChannelCatalogPool.Where(row => row.ChannelId == channelId).ExecuteDeleteAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var entry in picks)
        {
            _db.ChannelCatalogPool.Add(new ChannelCatalogPoolItem
            {
                ChannelId = channelId,
                JellyfinItemId = entry.Id,
                Kind = entry.Type,
                PickedAt = now
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    private async Task<List<Guid>> AskPicksAsync(
        Channel channel,
        IReadOnlyList<AiCatalogEntry> page,
        CancellationToken cancellationToken)
    {
        var tag = ChannelAiRules.ExtractLibraryTag(channel.FilterJson);
        var system = "You pick TV series and movies that belong on one cable channel. Reply with JSON only: {\"picks\":[1,4,9]} using the n numbers from the page. Do not invent titles. Skip anything that does not fit.";
        var user = JsonSerializer.Serialize(new
        {
            channel = channel.Name,
            rules = ChannelAiRules.GetBrief(tag),
            page = page.Select((c, index) => ToPageRow(c, index + 1))
        }, JsonOptions);
        var raw = await _llm.CompleteJsonAsync(
            FinTvRuntime.Current?.Configuration.Ai.DefaultProvider ?? AiProvider.OpenAi,
            system,
            user,
            cancellationToken);
        return ParsePickNumbers(raw, page);
    }

    private async Task<Dictionary<Guid, List<Guid>>> AskAssignmentsAsync(
        IReadOnlyList<Channel> channels,
        IReadOnlyList<AiCatalogEntry> page,
        CancellationToken cancellationToken)
    {
        var channelRows = channels.Select(c =>
        {
            var tag = ChannelAiRules.ExtractLibraryTag(c.FilterJson) ?? string.Empty;
            return new { id = c.Id, name = c.Name, tag, rules = ChannelAiRules.GetBrief(tag) };
        }).ToList();
        var system = "You assign each catalog title to every matching ChannelFlow channel. A title may match more than one channel. Reply JSON only: {\"assignments\":[{\"n\":1,\"tags\":[\"channelflow-flashback\"]}]}. Use the channel tag values. Skip titles that match none.";
        var user = JsonSerializer.Serialize(new
        {
            channels = channelRows,
            page = page.Select((c, index) => ToPageRow(c, index + 1))
        }, JsonOptions);
        var raw = await _llm.CompleteJsonAsync(
            FinTvRuntime.Current?.Configuration.Ai.DefaultProvider ?? AiProvider.OpenAi,
            system,
            user,
            cancellationToken);
        return ParseAssignments(raw, page, channels);
    }

    private static object ToPageRow(AiCatalogEntry entry, int n)
        => new
        {
            n,
            title = entry.Title,
            type = entry.Type,
            year = entry.Year,
            plot = entry.Plot,
            genres = entry.Genres,
            officialRating = entry.OfficialRating,
            studios = entry.Studios,
            libraryName = entry.LibraryName,
            runtimeMinutes = entry.RuntimeMinutes
        };

    private static List<Guid> ParsePickNumbers(string raw, IReadOnlyList<AiCatalogEntry> page)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFence(raw));
            if (!doc.RootElement.TryGetProperty("picks", out var picks) || picks.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<Guid>();
            foreach (var item in picks.EnumerateArray())
            {
                if (item.TryGetInt32(out var n) && n >= 1 && n <= page.Count)
                {
                    ids.Add(page[n - 1].Id);
                }
            }

            return ids;
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<Guid, List<Guid>> ParseAssignments(
        string raw,
        IReadOnlyList<AiCatalogEntry> page,
        IReadOnlyList<Channel> channels)
    {
        var byTag = channels
            .Select(c => (c.Id, Tag: ChannelAiRules.ExtractLibraryTag(c.FilterJson)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Tag))
            .ToLookup(x => x.Tag!, StringComparer.OrdinalIgnoreCase);
        var result = channels.ToDictionary(c => c.Id, _ => new List<Guid>());
        try
        {
            using var doc = JsonDocument.Parse(StripFence(raw));
            if (!doc.RootElement.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var row in assignments.EnumerateArray())
            {
                if (!row.TryGetProperty("n", out var nEl) || !nEl.TryGetInt32(out var n) || n < 1 || n > page.Count)
                {
                    continue;
                }

                if (!row.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var id = page[n - 1].Id;
                foreach (var tagEl in tags.EnumerateArray())
                {
                    var tag = tagEl.GetString();
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        continue;
                    }

                    foreach (var channel in byTag[tag])
                    {
                        result[channel.Id].Add(id);
                    }
                }
            }
        }
        catch
        {
            return result;
        }

        return result;
    }

    private static string StripFence(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                text = text[(start + 1)..end];
            }
        }

        return text.Trim();
    }

    private async Task<List<AiCatalogEntry>> LoadEligibleCatalogAsync(Channel channel, CancellationToken cancellationToken)
    {
        var all = await LoadAllLiveCatalogAsync(cancellationToken);
        return all.Where(entry => PassesHardFilters(channel, entry)).ToList();
    }

    private async Task<List<AiCatalogEntry>> LoadAllLiveCatalogAsync(CancellationToken cancellationToken)
    {
        var shows = await _db.TvShows.AsNoTracking().Where(row => !row.IsMissing).ToListAsync(cancellationToken);
        var movies = await _db.Movies.AsNoTracking().Where(row => !row.IsMissing).ToListAsync(cancellationToken);
        var clips = await _db.PastTenseNews.AsNoTracking().Where(row => !row.IsMissing).ToListAsync(cancellationToken);
        var firstYears = await LoadFirstEpisodeYearsAsync(shows.Select(s => s.Id).ToHashSet(), cancellationToken);
        var entries = new List<AiCatalogEntry>(shows.Count + movies.Count + clips.Count);
        foreach (var show in shows)
        {
            entries.Add(MapShow(show, firstYears));
        }

        foreach (var movie in movies)
        {
            entries.Add(MapMovie(movie));
        }

        foreach (var clip in clips)
        {
            entries.Add(MapClip(clip));
        }

        return entries
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<Guid, int>> LoadFirstEpisodeYearsAsync(
        HashSet<Guid> seriesIds,
        CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return [];
        }

        var rows = await _db.Episodes.AsNoTracking()
            .Where(e => !e.IsMissing && e.SeriesId != null && seriesIds.Contains(e.SeriesId.Value))
            .Select(e => new { e.SeriesId, e.PremiereDate, e.ProductionYear })
            .ToListAsync(cancellationToken);
        var map = new Dictionary<Guid, int>();
        foreach (var group in rows.GroupBy(r => r.SeriesId!.Value))
        {
            var year = group
                .Select(r => r.PremiereDate?.Year ?? r.ProductionYear)
                .Where(y => y is > 1888 and < 2100)
                .DefaultIfEmpty()
                .Min();
            if (year is int value and > 0)
            {
                map[group.Key] = value;
            }
        }

        return map;
    }

    private bool PassesHardFilters(Channel channel, AiCatalogEntry entry)
    {
        if (_holidays.IsHolidayChannel(channel))
        {
            var holiday = _holidays.GetActiveHoliday(_holidays.GetScheduleDateUtc(DateTime.UtcNow));
            if (holiday is null)
            {
                return false;
            }
        }

        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        if (yearConstraints is not null && !yearConstraints.ContainsYear(entry.Year))
        {
            return false;
        }

        var library = ChannelAiRules.GetLibraryConstraints(channel);
        if (library is not null)
        {
            var names = library.AllLibraryNames();
            if (!names.Any(name => string.Equals(name, entry.LibraryName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        var filter = FilterDefinition.Parse(channel.FilterJson);
        if (!string.IsNullOrWhiteSpace(filter?.MaxRating)
            && !RatingAtMost(entry.OfficialRating, filter.MaxRating))
        {
            return false;
        }

        var genre = ChannelAiRules.GetGenreConstraints(channel);
        if (genre is null)
        {
            return true;
        }

        var stub = new BaseItem
        {
            Name = entry.Title,
            Overview = entry.Plot,
            OfficialRating = entry.OfficialRating,
            Genres = entry.Genres.ToArray(),
            Studios = entry.Studios.ToArray(),
            Tags = entry.Tags.ToArray(),
            LibraryName = entry.LibraryName
        };
        return genre.MatchesItem(stub);
    }

    private static bool RatingAtMost(string? itemRating, string maxRating)
    {
        var item = RatingScore(itemRating);
        var max = RatingScore(maxRating);
        return item.HasValue && max.HasValue && item.Value <= max.Value;
    }

    private static int? RatingScore(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        return rating.Trim().ToUpperInvariant() switch
        {
            "TV-Y" => 1,
            "TV-Y7" => 2,
            "G" => 2,
            "TV-G" => 3,
            "PG" => 4,
            "TV-PG" => 4,
            "PG-13" => 5,
            "TV-14" => 5,
            "R" => 6,
            "TV-MA" => 6,
            "NC-17" => 7,
            _ => null
        };
    }

    private static List<List<AiCatalogEntry>> Paginate(List<AiCatalogEntry> catalog, int pageSize)
    {
        var pages = new List<List<AiCatalogEntry>>();
        for (var i = 0; i < catalog.Count; i += pageSize)
        {
            pages.Add(catalog.Skip(i).Take(pageSize).ToList());
        }

        return pages;
    }

    private static AiCatalogEntry MapShow(TvShowRow show, IReadOnlyDictionary<Guid, int> firstYears)
    {
        firstYears.TryGetValue(show.Id, out var firstYear);
        return MapRow(show, "Series", firstYear > 0 ? firstYear : show.PremiereDate?.Year ?? show.ProductionYear);
    }

    private static AiCatalogEntry MapMovie(MovieRow movie)
        => MapRow(movie, "Movie", movie.PremiereDate?.Year ?? movie.ProductionYear);

    private static AiCatalogEntry MapClip(PastTenseNewsRow clip)
        => MapRow(clip, "Clip", clip.PremiereDate?.Year ?? clip.ProductionYear);

    private static AiCatalogEntry MapRow(CatalogMediaRow row, string type, int? year)
    {
        var runtime = row.RuntimeTicks is long ticks && ticks > 0
            ? (int)Math.Max(1, Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes))
            : type == "Series" ? 30 : 90;
        return new AiCatalogEntry
        {
            Id = row.Id,
            Title = row.Name,
            Type = type,
            Year = year,
            PremiereDate = row.PremiereDate,
            RuntimeMinutes = runtime,
            Genres = ParseList(row.GenresJson),
            Tags = ParseList(row.TagsJson),
            Studios = ParseList(row.StudiosJson),
            Plot = Truncate(row.Plot),
            OfficialRating = AiCatalogManifestBuilder.NormalizeOfficialRating(row.OfficialRating),
            LibraryName = row.LibraryName
        };
    }

    private static List<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        try
        {
            return FinTvJson.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string? Truncate(string? plot)
    {
        if (string.IsNullOrWhiteSpace(plot))
        {
            return null;
        }

        return plot.Length <= 240 ? plot : plot[..240] + "...";
    }
}
