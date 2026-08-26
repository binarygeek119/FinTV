using System.Globalization;
using System.Security;
using FinTv;
using FinTv.Auth;
using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Builds Live TV guide metadata from Jellyfin library items for XMLTV output.
/// </summary>
public class GuideMetadataService
{
    private const int MaxOverviewLength = 500;

    private static readonly string[] PosterFileNames =
    [
        "poster.jpg", "poster.jpeg", "poster.png", "poster.webp",
        "folder.jpg", "folder.jpeg", "folder.png",
        "cover.jpg", "cover.jpeg", "cover.png"
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _http;
    private readonly Dictionary<Guid, string?> _posterPathCache = [];

    public GuideMetadataService(ILibraryManager libraryManager, IHttpClientFactory http)
    {
        _libraryManager = libraryManager;
        _http = http;
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
                PosterItemId = PosterItemId(item)
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
    /// Prefers the series poster for episodes, then the movie/episode image, then folder art next to the media.
    /// </summary>
    public string? GetPosterImagePath(Guid itemId)
    {
        if (_posterPathCache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var path = ResolvePosterImagePath(itemId);
        _posterPathCache[itemId] = path;
        return path;
    }

    /// <summary>
    /// Builds an absolute poster URL for XMLTV programme icons when a local poster file exists.
    /// </summary>
    public string? GetPosterUrlIfAvailable(string baseUrl, Guid? posterItemId)
    {
        if (!posterItemId.HasValue || posterItemId.Value == Guid.Empty)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(GetPosterImagePath(posterItemId.Value)))
        {
            return null;
        }

        return GetPosterUrl(baseUrl, posterItemId);
    }

    /// <summary>
    /// Resolves a poster file on disk, downloading it from Jellyfin when the sidecar is missing.
    /// </summary>
    public async Task<string?> GetOrFetchPosterImagePathAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var local = GetPosterImagePath(itemId);
        if (IsExistingFile(local))
        {
            return local;
        }

        var source = GetPosterSourceItem(itemId);
        var fetchId = source?.Id is { } id && id != Guid.Empty ? id : itemId;
        if (fetchId != itemId)
        {
            local = GetPosterImagePath(fetchId);
            if (IsExistingFile(local))
            {
                return local;
            }
        }

        return await CacheJellyfinPosterAsync(fetchId, cancellationToken).ConfigureAwait(false)
            ?? (fetchId != itemId
                ? await CacheJellyfinPosterAsync(itemId, cancellationToken).ConfigureAwait(false)
                : null);
    }

    /// <summary>
    /// Builds a same-origin poster URL for the ChannelFlow Web UI (cookie auth).
    /// </summary>
    public static string? GetUiPosterUrl(Guid? posterItemId)
    {
        if (!posterItemId.HasValue || posterItemId.Value == Guid.Empty)
        {
            return null;
        }

        return $"/api/guide/poster/{posterItemId.Value:N}";
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

        return $"{baseUrl.TrimEnd('/')}/iptv/poster/{posterItemId.Value:N}";
    }

    private async Task<string?> CacheJellyfinPosterAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (itemId == Guid.Empty)
        {
            return null;
        }

        var dataFolder = FinTvRuntime.Current?.DataFolder;
        var jellyfinUrl = FinTvRuntime.Current?.Configuration.JellyfinPluginUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(dataFolder) || string.IsNullOrWhiteSpace(jellyfinUrl))
        {
            return null;
        }

        var folder = Path.Combine(dataFolder, "posters");
        Directory.CreateDirectory(folder);
        foreach (var existing in Directory.EnumerateFiles(folder, itemId.ToString("N") + ".*"))
        {
            if (new FileInfo(existing).Length > 0)
            {
                _posterPathCache[itemId] = existing;
                return existing;
            }
        }

        try
        {
            var client = _http.CreateClient("JellyfinPlugin");
            var url = $"{jellyfinUrl}/Items/{itemId:N}/Images/Primary?maxHeight=720&quality=90";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var apiKey = PluginApiKey.Resolve();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 32)
            {
                return null;
            }

            var extension = mediaType?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg"
            };
            var dest = Path.Combine(folder, itemId.ToString("N") + extension);
            await File.WriteAllBytesAsync(dest, bytes, cancellationToken).ConfigureAwait(false);
            _posterPathCache[itemId] = dest;
            return dest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
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
        var posterItemId = PosterItemId(series) ?? PosterItemId(episode);

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
            PosterItemId = PosterItemId(movie)
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
            PosterItemId = PosterItemId(musicVideo)
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
            PosterItemId = PosterItemId(audio)
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

    private string? ResolvePosterImagePath(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var series = item is Episode episode ? ResolveSeries(episode) : item as Series;
        var found = FindPosterFile(series, item);
        if (!string.IsNullOrWhiteSpace(found))
        {
            return found;
        }

        return series is not null ? FindPosterFromSeriesEpisode(series.Id) : null;
    }

    private string? FindPosterFromSeriesEpisode(Guid seriesId)
    {
        var episode = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            Limit = 1
        }).Items.OfType<Episode>().FirstOrDefault();

        return episode is null ? null : FindPosterFile(episode);
    }

    private static Guid? PosterItemId(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        return item.Id == Guid.Empty ? null : item.Id;
    }

    private static string? FindPosterFile(params BaseItem?[] items)
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (IsExistingFile(item.PrimaryImagePath))
            {
                return item.PrimaryImagePath;
            }

            var sidecar = FindSidecarPoster(item.Path, walkParents: true);
            if (!string.IsNullOrWhiteSpace(sidecar))
            {
                return sidecar;
            }
        }

        return null;
    }

    private static string? FindSidecarPoster(string? mediaPath, bool walkParents)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        var start = File.Exists(mediaPath) ? Path.GetDirectoryName(mediaPath) : mediaPath;
        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
        {
            return null;
        }

        var current = start;
        var levels = walkParents ? 3 : 1;
        for (var i = 0; i < levels; i++)
        {
            var named = FindNamedPoster(current, File.Exists(mediaPath) ? Path.GetFileNameWithoutExtension(mediaPath) : null);
            if (!string.IsNullOrWhiteSpace(named))
            {
                return named;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return null;
    }

    private static string? FindNamedPoster(string folder, string? mediaStem)
    {
        var wanted = new List<string>();
        if (!string.IsNullOrWhiteSpace(mediaStem))
        {
            wanted.AddRange([mediaStem + "-poster.jpg", mediaStem + "-poster.png", mediaStem + ".jpg", mediaStem + ".png"]);
        }

        wanted.AddRange(PosterFileNames);

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (wanted.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return file;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static bool IsExistingFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

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
