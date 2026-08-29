using FinTv.Configuration;
using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Builds compact Jellyfin catalog manifests for AI lineup generation.
/// </summary>
public class AiCatalogManifestBuilder
{
    private readonly JellyfinCatalogService _catalog;

    public AiCatalogManifestBuilder(JellyfinCatalogService catalog)
    {
        _catalog = catalog;
    }

    public AiCatalogManifest Build(Channel channel, IReadOnlyList<AiCatalogEntry>? pool = null)
    {
        var catalogMode = JellyfinCatalogService.ResolveCatalogMode(channel);
        if (pool is { Count: > 0 })
        {
            return new AiCatalogManifest
            {
                CatalogMode = catalogMode,
                TotalAvailable = pool.Count,
                IncludedInPrompt = pool.Count,
                TagMatchedCount = pool.Count,
                Catalog = pool.ToList()
            };
        }
        var mapMode = PastTenseNewsCatalog.IsPastTenseNewsChannel(channel)
            ? ChannelCatalogMode.Mixed
            : catalogMode;
        var maxItems = FinTvRuntime.Current?.Configuration.Ai.MaxCatalogItemsInPrompt ?? 250;
        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        var genreConstraints = ChannelAiRules.GetGenreConstraints(channel);
        var browseStats = _catalog.BrowseForAiManifestWithStats(channel, catalogMode, maxItems);
        var totalAvailable = browseStats.AfterConstraintCount;
        var items = browseStats.Items;

        var entries = items
            .Select(item => MapEntry(item, mapMode, yearConstraints, genreConstraints))
            .Where(e => e is not null)
            .Cast<AiCatalogEntry>()
            .OrderBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AiCatalogManifest
        {
            CatalogMode = catalogMode,
            TotalAvailable = totalAvailable,
            IncludedInPrompt = entries.Count,
            TagMatchedCount = browseStats.TagMatchedCount,
            Catalog = entries
        };
    }

    private AiCatalogEntry? MapEntry(
        BaseItem item,
        ChannelCatalogMode catalogMode,
        ChannelCatalogYearConstraints? yearConstraints,
        ChannelCatalogGenreConstraints? genreConstraints)
    {
        if (item is Series series)
        {
            if (catalogMode == ChannelCatalogMode.MovieOnly || catalogMode == ChannelCatalogMode.MusicVideoOnly)
            {
                return null;
            }

            if (yearConstraints is not null && !_catalog.MatchesYearConstraints(series, yearConstraints))
            {
                return null;
            }

            if (genreConstraints is not null && !_catalog.MatchesGenreConstraints(series, genreConstraints))
            {
                return null;
            }

            return new AiCatalogEntry
            {
                Id = series.Id,
                Title = series.Name,
                Type = "Series",
                Year = _catalog.GetCatalogReleaseYear(series, yearConstraints),
                PremiereDate = series.PremiereDate,
                RuntimeMinutes = EstimateSeriesRuntimeMinutes(series),
                Genres = series.Genres?.ToList() ?? new List<string>(),
                Tags = series.Tags?.ToList() ?? new List<string>(),
                Plot = TruncatePlot(series.Overview),
                OfficialRating = NormalizeOfficialRating(series.OfficialRating),
                Studios = series.Studios?.ToList() ?? new List<string>(),
                LibraryName = series.LibraryName
            };
        }

        if (item is Movie movie)
        {
            if (catalogMode is ChannelCatalogMode.TvOnly or ChannelCatalogMode.MusicVideoOnly)
            {
                return null;
            }

            if (yearConstraints is not null && !_catalog.MatchesYearConstraints(movie, yearConstraints))
            {
                return null;
            }

            if (genreConstraints is not null && !_catalog.MatchesGenreConstraints(movie, genreConstraints))
            {
                return null;
            }

            return new AiCatalogEntry
            {
                Id = movie.Id,
                Title = movie.Name,
                Type = "Movie",
                Year = JellyfinCatalogService.GetReleaseYear(movie),
                PremiereDate = movie.PremiereDate,
                RuntimeMinutes = _catalog.GetRuntimeMinutes(movie),
                Genres = movie.Genres?.ToList() ?? new List<string>(),
                Tags = movie.Tags?.ToList() ?? new List<string>(),
                Plot = TruncatePlot(movie.Overview),
                OfficialRating = NormalizeOfficialRating(movie.OfficialRating),
                Studios = movie.Studios?.ToList() ?? new List<string>(),
                LibraryName = movie.LibraryName
            };
        }

        if (item.Kind == BaseItemKind.Video)
        {
            if (catalogMode == ChannelCatalogMode.MusicVideoOnly)
            {
                return null;
            }

            return new AiCatalogEntry
            {
                Id = item.Id,
                Title = item.Name,
                Type = "Clip",
                Year = JellyfinCatalogService.GetReleaseYear(item),
                PremiereDate = item.PremiereDate,
                RuntimeMinutes = _catalog.GetRuntimeMinutes(item),
                Genres = item.Genres?.ToList() ?? new List<string>(),
                Tags = item.Tags?.ToList() ?? new List<string>(),
                Plot = TruncatePlot(item.Overview),
                OfficialRating = NormalizeOfficialRating(item.OfficialRating)
            };
        }

        if (item is MusicVideo musicVideo)
        {
            if (catalogMode is ChannelCatalogMode.TvOnly or ChannelCatalogMode.MovieOnly)
            {
                return null;
            }

            return new AiCatalogEntry
            {
                Id = musicVideo.Id,
                Title = musicVideo.Name,
                Type = "MusicVideo",
                Year = musicVideo.ProductionYear,
                RuntimeMinutes = _catalog.GetRuntimeMinutes(musicVideo),
                Genres = musicVideo.Genres?.ToList() ?? new List<string>(),
                Tags = musicVideo.Tags?.ToList() ?? new List<string>(),
                OfficialRating = NormalizeOfficialRating(musicVideo.OfficialRating)
            };
        }

        return null;
    }

    private static int EstimateSeriesRuntimeMinutes(Series series)
    {
        if (series.RunTimeTicks.HasValue)
        {
            var minutes = TimeSpan.FromTicks(series.RunTimeTicks.Value).TotalMinutes;
            if (minutes is > 5 and <= 90)
            {
                return (int)Math.Max(1, Math.Round(minutes));
            }
        }

        return 30;
    }

    private static string? TruncatePlot(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        return overview.Length <= 240 ? overview : overview[..240] + "...";
    }

    internal static string? NormalizeOfficialRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var key = rating.Trim().ToUpperInvariant();
        return key is "UR" or "NR" or "UNRATED" or "NOT RATED" or "NOTRATED" or "N/R"
            ? "UR"
            : rating.Trim();
    }
}

public class AiCatalogManifest
{
    public ChannelCatalogMode CatalogMode { get; set; }

    public int TotalAvailable { get; set; }

    public int IncludedInPrompt { get; set; }

    public int TagMatchedCount { get; set; }

    public List<AiCatalogEntry> Catalog { get; set; } = new();
}

public class AiCatalogEntry
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int? Year { get; set; }

    public DateTime? PremiereDate { get; set; }

    public int RuntimeMinutes { get; set; }

    public List<string> Genres { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    public string? Plot { get; set; }

    public string? OfficialRating { get; set; }

    public List<string> Studios { get; set; } = new();

    public string? LibraryName { get; set; }
}
