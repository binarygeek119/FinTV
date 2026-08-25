using System.Text.Json;
using FinTv;
using FinTv.Api;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Writes synced Jellyfin items into TvShows, Episodes, Movies, Music, MusicVideos, and PastTenseNews.
/// </summary>
public sealed class CatalogTypedStore
{
    private readonly FinTvDbContext _db;

    public CatalogTypedStore(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task UpsertAsync(IReadOnlyList<CatalogItemDto> items, bool replaceAll, CancellationToken cancellationToken)
    {
        _ = replaceAll;
        await CatalogSchema.EnsureEpisodesTableAsync(_db, cancellationToken);
        var incomingIds = items.Select(item => item.Id).ToHashSet();

        var tv = await _db.TvShows.Where(row => incomingIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken);
        var episodes = await _db.Episodes.Where(row => incomingIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken);
        var movies = await _db.Movies.Where(row => incomingIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken);
        var music = await _db.Music.Where(row => incomingIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken);
        var videos = await _db.MusicVideos.Where(row => incomingIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken);
        var news = await _db.PastTenseNews.Where(row => incomingIds.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken);

        foreach (var item in items)
        {
            var target = Classify(item);
            if (target is null)
            {
                continue;
            }

            RemoveIfPresent(tv, _db.TvShows, item.Id, keep: target == CatalogTable.TvShows);
            RemoveIfPresent(episodes, _db.Episodes, item.Id, keep: target == CatalogTable.Episodes);
            RemoveIfPresent(movies, _db.Movies, item.Id, keep: target == CatalogTable.Movies);
            RemoveIfPresent(music, _db.Music, item.Id, keep: target == CatalogTable.Music);
            RemoveIfPresent(videos, _db.MusicVideos, item.Id, keep: target == CatalogTable.MusicVideos);
            RemoveIfPresent(news, _db.PastTenseNews, item.Id, keep: target == CatalogTable.PastTenseNews);

            switch (target)
            {
                case CatalogTable.TvShows:
                    Apply(GetOrAdd(tv, _db.TvShows, item.Id), item);
                    break;
                case CatalogTable.Episodes:
                    ApplyEpisode(GetOrAdd(episodes, _db.Episodes, item.Id), item);
                    break;
                case CatalogTable.Movies:
                    Apply(GetOrAdd(movies, _db.Movies, item.Id), item);
                    break;
                case CatalogTable.Music:
                    ApplyMusic(GetOrAdd(music, _db.Music, item.Id), item);
                    break;
                case CatalogTable.MusicVideos:
                    ApplyMusicVideo(GetOrAdd(videos, _db.MusicVideos, item.Id), item);
                    break;
                case CatalogTable.PastTenseNews:
                    ApplyNews(GetOrAdd(news, _db.PastTenseNews, item.Id), item);
                    break;
            }
        }
    }

    public async Task BackfillFromMediaItemsAsync(CancellationToken cancellationToken)
    {
        var hasTyped = await _db.TvShows.AnyAsync(cancellationToken)
            || await _db.Episodes.AnyAsync(cancellationToken)
            || await _db.Movies.AnyAsync(cancellationToken)
            || await _db.Music.AnyAsync(cancellationToken)
            || await _db.MusicVideos.AnyAsync(cancellationToken)
            || await _db.PastTenseNews.AnyAsync(cancellationToken);
        if (hasTyped)
        {
            return;
        }

        var rows = await _db.MediaItems.AsNoTracking().Include(item => item.Chapters).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        const int batchSize = 250;
        for (var offset = 0; offset < rows.Count; offset += batchSize)
        {
            var batch = rows.Skip(offset).Take(batchSize).Select(ToDto).ToList();
            await UpsertAsync(batch, replaceAll: false, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Writes normalized 16:9 / 4:3 / other aspect values onto existing catalog rows.
    /// </summary>
    public async Task NormalizeAspectRatiosAsync(CancellationToken cancellationToken)
    {
        var typed = new List<CatalogMediaRow>();
        typed.AddRange(await NormalizeRowsAsync(_db.TvShows, cancellationToken));
        typed.AddRange(await NormalizeRowsAsync(_db.Episodes, cancellationToken));
        typed.AddRange(await NormalizeRowsAsync(_db.Movies, cancellationToken));
        typed.AddRange(await NormalizeRowsAsync(_db.Music, cancellationToken));
        typed.AddRange(await NormalizeRowsAsync(_db.MusicVideos, cancellationToken));
        typed.AddRange(await NormalizeRowsAsync(_db.PastTenseNews, cancellationToken));

        var byId = new Dictionary<Guid, (int? Width, int? Height, string? AspectRatio)>();
        foreach (var row in typed)
        {
            byId[row.Id] = (row.Width, row.Height, row.AspectRatio);
        }

        var mediaItems = await _db.MediaItems.ToListAsync(cancellationToken);
        foreach (var item in mediaItems)
        {
            if (byId.TryGetValue(item.Id, out var typedRow))
            {
                item.Width ??= typedRow.Width;
                item.Height ??= typedRow.Height;
            }

            var classified = VideoAspectFormat.Classify(item.AspectRatio, item.Width, item.Height);
            if (!string.Equals(item.AspectRatio, classified, StringComparison.Ordinal))
            {
                item.AspectRatio = classified;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<List<T>> NormalizeRowsAsync<T>(DbSet<T> set, CancellationToken cancellationToken)
        where T : CatalogMediaRow
    {
        var rows = await set.ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var classified = VideoAspectFormat.Classify(row.AspectRatio, row.Width, row.Height);
            if (!string.Equals(row.AspectRatio, classified, StringComparison.Ordinal))
            {
                row.AspectRatio = classified;
            }
        }

        return rows;
    }

    private static CatalogTable? Classify(CatalogItemDto item)
    {
        if (IsPastTenseNews(item))
        {
            return CatalogTable.PastTenseNews;
        }

        return item.Kind switch
        {
            BaseItemKind.Series => CatalogTable.TvShows,
            BaseItemKind.Episode => CatalogTable.Episodes,
            BaseItemKind.Movie or BaseItemKind.Video => CatalogTable.Movies,
            BaseItemKind.Audio => CatalogTable.Music,
            BaseItemKind.MusicVideo => CatalogTable.MusicVideos,
            _ => null
        };
    }

    private static void RemoveIfPresent<T>(Dictionary<Guid, T> byId, DbSet<T> set, Guid id, bool keep)
        where T : class
    {
        if (keep || !byId.Remove(id, out var row))
        {
            return;
        }

        set.Remove(row);
    }

    private static T GetOrAdd<T>(Dictionary<Guid, T> byId, DbSet<T> set, Guid id)
        where T : CatalogMediaRow, new()
    {
        if (byId.TryGetValue(id, out var row))
        {
            return row;
        }

        row = new T { Id = id };
        set.Add(row);
        byId[id] = row;
        return row;
    }

    private static void ApplyEpisode(EpisodeRow row, CatalogItemDto item)
    {
        Apply(row, item);
        row.SeriesId = item.SeriesId;
        row.SeriesName = item.SeriesName;
        row.SeasonId = item.SeasonId;
        row.SeasonName = item.SeasonName;
        row.SeasonNumber = item.ParentIndexNumber;
        row.EpisodeNumber = item.IndexNumber;
    }

    private static void ApplyMusic(MusicRow row, CatalogItemDto item)
    {
        Apply(row, item);
        row.Album = item.Album;
        row.AlbumArtist = item.AlbumArtist;
        row.ArtistsJson = JsonSerializer.Serialize(item.Artists ?? []);
        row.TrackNumber = item.IndexNumber;
        row.DiscNumber = item.ParentIndexNumber;
    }

    private static void ApplyMusicVideo(MusicVideoRow row, CatalogItemDto item)
    {
        Apply(row, item);
        row.Album = item.Album;
        row.ArtistsJson = JsonSerializer.Serialize(item.Artists ?? []);
    }

    private static void ApplyNews(PastTenseNewsRow row, CatalogItemDto item)
    {
        Apply(row, item);
        row.SeriesId = item.SeriesId;
        row.SeriesName = item.SeriesName;
        row.SeasonNumber = item.ParentIndexNumber;
        row.EpisodeNumber = item.IndexNumber;
    }

    private static void Apply(CatalogMediaRow row, CatalogItemDto item)
    {
        var providers = item.ProviderIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        row.Name = item.Name ?? string.Empty;
        row.SortName = item.SortName;
        row.Plot = item.Overview ?? item.Plot;
        row.OfficialRating = item.OfficialRating;
        row.CommunityRating = item.CommunityRating;
        row.CriticRating = item.CriticRating;
        row.ProductionYear = item.ProductionYear;
        row.PremiereDate = item.PremiereDate;
        row.RuntimeTicks = item.RuntimeTicks;
        row.Format = item.Format ?? item.Container;
        row.VideoCodec = item.VideoCodec;
        row.AudioCodec = item.AudioCodec;
        row.Width = item.Width;
        row.Height = item.Height;
        row.AspectRatio = VideoAspectFormat.Classify(item.AspectRatio, item.Width, item.Height);
        row.Path = item.Path;
        row.JellyfinItemId = item.Id;
        row.ImdbId = FindProvider(providers, "imdb", "imdbid");
        row.TmdbId = FindProvider(providers, "tmdb", "tmdbid");
        row.TvdbId = FindProvider(providers, "tvdb", "tvdbid");
        row.MusicBrainzId = FindProvider(providers, "musicbrainztrack", "musicbrainz", "musicbrainzalbum");
        row.ProviderIdsJson = JsonSerializer.Serialize(providers);
        row.LibraryId = item.LibraryId;
        row.LibraryName = item.LibraryName;
        row.PrimaryImagePath = item.PrimaryImagePath;
        row.GenresJson = JsonSerializer.Serialize(item.Genres ?? []);
        row.StarsJson = JsonSerializer.Serialize(StarsFrom(item));
        row.StudiosJson = JsonSerializer.Serialize(item.Studios ?? []);
        row.TagsJson = JsonSerializer.Serialize(item.Tags ?? []);
        row.ChaptersJson = JsonSerializer.Serialize((item.Chapters ?? []).Select(chapter => new
        {
            startPositionTicks = chapter.StartPositionTicks,
            name = chapter.Name
        }));
        row.SyncedAt = DateTime.UtcNow;
        row.IsMissing = false;
        row.MissingSince = null;
    }

    private static List<string> StarsFrom(CatalogItemDto item)
    {
        if (item.Stars is { Count: > 0 })
        {
            return item.Stars;
        }

        return (item.People ?? [])
            .Select(person => person.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPastTenseNews(CatalogItemDto item)
        => PastTenseNewsCatalog.IsHomeMovieItem(
            item.LibraryName,
            item.CollectionType,
            item.LibraryId,
            item.Kind,
            FinTvRuntime.Current?.Configuration.JellyfinLibraries.HomeVideoLibraryIds);

    private static string? FindProvider(Dictionary<string, string> providers, params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = providers.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static CatalogItemDto ToDto(MediaItem item)
        => new()
        {
            Id = item.Id,
            Name = item.Name,
            SortName = item.SortName,
            Overview = item.Overview,
            Kind = item.Kind,
            Path = item.Path,
            SeriesId = item.SeriesId,
            SeriesName = item.SeriesName,
            SeasonId = item.SeasonId,
            SeasonName = item.SeasonName,
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            OfficialRating = item.OfficialRating,
            RuntimeTicks = item.RuntimeTicks,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,
            LibraryId = item.LibraryId,
            LibraryName = item.LibraryName,
            PrimaryImagePath = item.PrimaryImagePath,
            Width = item.Width,
            Height = item.Height,
            AspectRatio = item.AspectRatio,
            Genres = ReadJsonArray(item.GenresJson),
            Tags = ReadJsonArray(item.TagsJson),
            Studios = ReadJsonArray(item.StudiosJson),
            Chapters = item.Chapters.Select(chapter => new CatalogChapterDto
            {
                StartPositionTicks = chapter.StartPositionTicks,
                Name = chapter.Name
            }).ToList()
        };

    private static List<string> ReadJsonArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private enum CatalogTable
    {
        TvShows,
        Episodes,
        Movies,
        Music,
        MusicVideos,
        PastTenseNews
    }
}
