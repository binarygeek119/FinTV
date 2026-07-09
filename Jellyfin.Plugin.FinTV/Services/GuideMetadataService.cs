using System.Globalization;
using System.Security;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Builds Live TV guide metadata from Jellyfin library items for XMLTV output.
/// </summary>
public class GuideMetadataService
{
    private const int MaxOverviewLength = 500;

    private readonly ILibraryManager _libraryManager;

    public GuideMetadataService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Resolves guide metadata for a batch of Jellyfin item identifiers.
    /// </summary>
    public Dictionary<Guid, GuideProgramMetadata> ResolveBatch(IEnumerable<Guid?> ids)
    {
        var result = new Dictionary<Guid, GuideProgramMetadata>();
        foreach (var id in ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            result[id] = BuildFromItem(item);
        }

        return result;
    }

    /// <summary>
    /// Builds guide metadata for one Jellyfin library item.
    /// </summary>
    public GuideProgramMetadata BuildFromItem(BaseItem item)
    {
        return item switch
        {
            Episode episode => BuildEpisodeMetadata(episode),
            Movie movie => BuildMovieMetadata(movie),
            MusicVideo musicVideo => BuildMusicVideoMetadata(musicVideo),
            Audio audio => BuildAudioMetadata(audio),
            _ => new GuideProgramMetadata
            {
                Title = item.Name,
                PosterItemId = item.HasImage(ImageType.Primary) ? item.Id : null
            }
        };
    }

    /// <summary>
    /// Gets the Jellyfin item whose primary image should be served as the programme poster.
    /// </summary>
    public BaseItem? GetPosterSourceItem(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        if (item is Episode episode)
        {
            return ResolveSeries(episode) ?? item;
        }

        return item;
    }

    /// <summary>
    /// Gets the filesystem path for a programme poster, if available.
    /// </summary>
    public string? GetPosterImagePath(Guid itemId)
    {
        var source = GetPosterSourceItem(itemId);
        if (source is null || !source.HasImage(ImageType.Primary))
        {
            return null;
        }

        return source.GetImagePath(ImageType.Primary);
    }

    /// <summary>
    /// Builds an absolute poster URL for XMLTV programme icons.
    /// </summary>
    public static string? GetPosterUrl(string baseUrl, Guid? posterItemId)
    {
        if (!posterItemId.HasValue || posterItemId.Value == Guid.Empty)
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/FinTV/iptv/poster/{posterItemId.Value:N}";
    }

    /// <summary>
    /// Formats season and episode numbers for XMLTV onscreen display.
    /// </summary>
    public static string FormatOnScreen(int? season, int? episode)
    {
        if (!season.HasValue && !episode.HasValue)
        {
            return string.Empty;
        }

        if (season.HasValue && episode.HasValue)
        {
            return string.Create(CultureInfo.InvariantCulture, $"S{season.Value:D2}E{episode.Value:D2}");
        }

        if (season.HasValue)
        {
            return string.Create(CultureInfo.InvariantCulture, $"S{season.Value:D2}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"E{episode!.Value:D2}");
    }

    /// <summary>
    /// Formats season and episode numbers for XMLTV xmltv_ns (zero-based).
    /// </summary>
    public static string FormatXmlTvNs(int? season, int? episode)
    {
        var seasonPart = season.HasValue ? Math.Max(0, season.Value - 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
        var episodePart = episode.HasValue ? Math.Max(0, episode.Value - 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"{seasonPart}.{episodePart}.";
    }

    /// <summary>
    /// Escapes text for safe inclusion in XMLTV elements.
    /// </summary>
    public static string EscapeXmlText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : SecurityElement.Escape(value);
    }

    /// <summary>
    /// Builds a guide-friendly playout title for admin and fallback EPG display.
    /// </summary>
    public string BuildPlayoutTitle(BaseItem item)
    {
        if (item is Episode episode)
        {
            var series = ResolveSeries(episode);
            var season = episode.ParentIndexNumber;
            var episodeNumber = episode.IndexNumber;
            var onScreen = FormatOnScreen(season, episodeNumber);
            if (series is not null && !string.IsNullOrWhiteSpace(onScreen))
            {
                return $"{series.Name} · {onScreen} · {episode.Name}";
            }

            if (series is not null)
            {
                return $"{series.Name} · {episode.Name}";
            }
        }

        return item.Name;
    }

    private GuideProgramMetadata BuildEpisodeMetadata(Episode episode)
    {
        var series = ResolveSeries(episode);
        var season = episode.ParentIndexNumber;
        var episodeNumber = episode.IndexNumber;
        var overview = TruncateOverview(episode.Overview);
        if (string.IsNullOrWhiteSpace(overview) && series is not null)
        {
            overview = TruncateOverview(series.Overview);
        }

        var genres = CollectGenres(episode, series);
        Guid? posterItemId = null;
        if (series?.HasImage(ImageType.Primary) == true)
        {
            posterItemId = series.Id;
        }
        else if (episode.HasImage(ImageType.Primary))
        {
            posterItemId = episode.Id;
        }

        return new GuideProgramMetadata
        {
            Title = series?.Name ?? episode.Name,
            SubTitle = series is not null ? episode.Name : null,
            Description = overview,
            SeasonNumber = season,
            EpisodeNumber = episodeNumber,
            EpisodeOnScreen = FormatOnScreen(season, episodeNumber),
            EpisodeXmlTvNs = season.HasValue || episodeNumber.HasValue
                ? FormatXmlTvNs(season, episodeNumber)
                : null,
            Categories = genres,
            IsMovie = false,
            IsSeries = season.HasValue || episodeNumber.HasValue,
            ProductionYear = episode.ProductionYear ?? series?.ProductionYear,
            OfficialRating = episode.OfficialRating ?? series?.OfficialRating,
            PosterItemId = posterItemId
        };
    }

    private GuideProgramMetadata BuildMovieMetadata(Movie movie)
    {
        var categories = new List<string> { "Movie" };
        categories.AddRange(movie.Genres.Where(g => !string.IsNullOrWhiteSpace(g)));

        return new GuideProgramMetadata
        {
            Title = movie.Name,
            Description = TruncateOverview(movie.Overview),
            Categories = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IsMovie = true,
            IsSeries = false,
            ProductionYear = movie.ProductionYear,
            OfficialRating = movie.OfficialRating,
            PosterItemId = movie.HasImage(ImageType.Primary) ? movie.Id : null
        };
    }

    private static GuideProgramMetadata BuildMusicVideoMetadata(MusicVideo musicVideo)
    {
        var categories = musicVideo.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        return new GuideProgramMetadata
        {
            Title = musicVideo.Name,
            Description = TruncateOverview(musicVideo.Overview),
            Categories = categories,
            ProductionYear = musicVideo.ProductionYear,
            PosterItemId = musicVideo.HasImage(ImageType.Primary) ? musicVideo.Id : null
        };
    }

    private static GuideProgramMetadata BuildAudioMetadata(Audio audio)
    {
        return new GuideProgramMetadata
        {
            Title = audio.Name,
            Description = TruncateOverview(audio.Overview),
            Categories = audio.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList(),
            ProductionYear = audio.ProductionYear,
            PosterItemId = audio.HasImage(ImageType.Primary) ? audio.Id : null
        };
    }

    private Series? ResolveSeries(Episode episode)
    {
        if (episode.SeriesId == Guid.Empty)
        {
            return episode.Series;
        }

        return _libraryManager.GetItemById(episode.SeriesId) as Series ?? episode.Series;
    }

    private static List<string> CollectGenres(Episode episode, Series? series)
    {
        var genres = episode.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (genres.Count == 0 && series is not null)
        {
            genres = series.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        }

        return genres;
    }

    private static string? TruncateOverview(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        var trimmed = overview.Trim();
        if (trimmed.Length <= MaxOverviewLength)
        {
            return trimmed;
        }

        return trimmed[..(MaxOverviewLength - 3)] + "...";
    }
}

/// <summary>
/// Guide metadata for one scheduled programme.
/// </summary>
public sealed class GuideProgramMetadata
{
    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? Description { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? EpisodeOnScreen { get; set; }

    public string? EpisodeXmlTvNs { get; set; }

    public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();

    public bool IsMovie { get; set; }

    public bool IsSeries { get; set; }

    public int? ProductionYear { get; set; }

    public string? OfficialRating { get; set; }

    public Guid? PosterItemId { get; set; }

    public string? IconUrl { get; set; }

    public string Language { get; set; } = "en";
}
