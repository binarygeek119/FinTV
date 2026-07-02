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
        var totalAvailable = _catalog.CountForAiManifest(channel, catalogMode);
        var items = _catalog.BrowseForAiManifest(channel, catalogMode, maxItems);

        var entries = items
            .Select(item => MapEntry(item, catalogMode))
            .Where(e => e is not null)
            .Cast<AiCatalogEntry>()
            .ToList();

        return new AiCatalogManifest
        {
            CatalogMode = catalogMode,
            TotalAvailable = totalAvailable,
            IncludedInPrompt = entries.Count,
            Catalog = entries
        };
    }

    private AiCatalogEntry? MapEntry(BaseItem item, ChannelCatalogMode catalogMode)
    {
        if (item is Series series)
        {
            return new AiCatalogEntry
            {
                Id = series.Id,
                Title = series.Name,
                Type = "Series",
                Year = series.ProductionYear,
                RuntimeMinutes = EstimateSeriesRuntimeMinutes(series),
                Genres = series.Genres?.ToList() ?? new List<string>(),
                Tags = series.Tags?.ToList() ?? new List<string>()
            };
        }

        if (item is Movie movie)
        {
            if (catalogMode == ChannelCatalogMode.TvOnly)
            {
                return null;
            }

            return new AiCatalogEntry
            {
                Id = movie.Id,
                Title = movie.Name,
                Type = "Movie",
                Year = movie.ProductionYear,
                RuntimeMinutes = _catalog.GetRuntimeMinutes(movie),
                Genres = movie.Genres?.ToList() ?? new List<string>(),
                Tags = movie.Tags?.ToList() ?? new List<string>()
            };
        }

        if (item is MusicVideo musicVideo)
        {
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
}

public class AiCatalogManifest
{
    public ChannelCatalogMode CatalogMode { get; set; }

    public int TotalAvailable { get; set; }

    public int IncludedInPrompt { get; set; }

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
}
