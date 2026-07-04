using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.FinTV.Services;

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

    public AiCatalogManifest Build(Channel channel)
    {
        var catalogMode = JellyfinCatalogService.ResolveCatalogMode(channel);
        var maxItems = Plugin.Instance?.Configuration.Ai.MaxCatalogItemsInPrompt ?? 250;
        var yearConstraints = ChannelAiRules.GetYearConstraints(channel);
        var genreConstraints = ChannelAiRules.GetGenreConstraints(channel);
        var browseStats = _catalog.BrowseForAiManifestWithStats(channel, catalogMode, maxItems);
        var totalAvailable = browseStats.AfterConstraintCount;
        var items = browseStats.Items;

        var entries = items
            .Select(item => MapEntry(item, catalogMode, yearConstraints, genreConstraints))
            .Where(e => e is not null)
            .Cast<AiCatalogEntry>()
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
                RuntimeMinutes = EstimateSeriesRuntimeMinutes(series),
                Genres = series.Genres?.ToList() ?? new List<string>(),
                Tags = series.Tags?.ToList() ?? new List<string>(),
                Plot = TruncatePlot(series.Overview)
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
                RuntimeMinutes = _catalog.GetRuntimeMinutes(movie),
                Genres = movie.Genres?.ToList() ?? new List<string>(),
                Tags = movie.Tags?.ToList() ?? new List<string>(),
                Plot = TruncatePlot(movie.Overview)
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
                Tags = musicVideo.Tags?.ToList() ?? new List<string>()
            };
        }

        return null;
    }

    private static int EstimateSeriesRuntimeMinutes(Series series)
    {
        if (series.RunTimeTicks.HasValue)
        {
            return (int)Math.Max(1, Math.Round(TimeSpan.FromTicks(series.RunTimeTicks.Value).TotalMinutes));
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

    public int RuntimeMinutes { get; set; }

    public List<string> Genres { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    public string? Plot { get; set; }
}
