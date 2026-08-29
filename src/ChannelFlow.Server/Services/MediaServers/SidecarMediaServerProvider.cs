using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FinTv.Api;
using FinTv.Domain;

namespace FinTv.Services.MediaServers;

public sealed class SidecarMediaServerProvider : MediaServerProviderBase
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".webm", ".mov", ".wmv", ".mpg", ".mpeg"
    };

    private readonly CatalogSyncProgress _progress;

    public SidecarMediaServerProvider(CatalogSyncProgress progress)
    {
        _progress = progress;
    }

    public override MediaServerKind Kind => MediaServerKind.Sidecar;

    public override bool CanSync => true;

    public override Task<MediaServerHealthResult> TestAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        var root = connection.SidecarRoot?.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            return Task.FromResult(new MediaServerHealthResult
            {
                Ok = false,
                Message = "Choose a local folder ChannelFlow can read."
            });
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(new MediaServerHealthResult
            {
                Ok = false,
                Message = "Folder does not exist: " + root
            });
        }

        var files = 0;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Count(path => VideoExtensions.Contains(Path.GetExtension(path)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new MediaServerHealthResult
            {
                Ok = false,
                Message = "Could not read the folder. " + ex.Message
            });
        }

        return Task.FromResult(new MediaServerHealthResult
        {
            Ok = true,
            ServerName = connection.Name,
            Message = "Folder is readable. Found " + files + " video file" + (files == 1 ? "" : "s") + "."
        });
    }

    public override Task<IReadOnlyList<MediaServerRemoteLibrary>> ListLibrariesAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        var root = connection.SidecarRoot?.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<MediaServerRemoteLibrary>>(Array.Empty<MediaServerRemoteLibrary>());
        }

        var libraries = new List<MediaServerRemoteLibrary>
        {
            new()
            {
                ExternalId = "root",
                Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    is { Length: > 0 } name
                    ? name
                    : "Sidecar",
                CollectionType = "mixed"
            }
        };

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                libraries.Add(new MediaServerRemoteLibrary
                {
                    ExternalId = Path.GetFileName(dir),
                    Name = Path.GetFileName(dir),
                    CollectionType = GuessCollectionType(Path.GetFileName(dir))
                });
            }
        }
        catch (Exception)
        {
            // Root library is enough.
        }

        return Task.FromResult<IReadOnlyList<MediaServerRemoteLibrary>>(libraries);
    }

    public override Task<IReadOnlyList<CatalogItemDto>> ImportItemsAsync(
        MediaServerConnection connection,
        IReadOnlyList<MediaServerLibrary> libraries,
        CancellationToken cancellationToken)
    {
        var root = connection.SidecarRoot?.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<CatalogItemDto>>(Array.Empty<CatalogItemDto>());
        }

        var enabled = libraries.Where(library => library.SyncEnabled).ToList();
        var items = new List<CatalogItemDto>();
        var seriesByFolder = new Dictionary<string, CatalogItemDto>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)));
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<CatalogItemDto>>(Array.Empty<CatalogItemDto>());
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path);
            var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var library = MatchLibrary(enabled, top);
            if (library is null && enabled.All(row => row.ExternalId != "root"))
            {
                continue;
            }

            library ??= enabled.FirstOrDefault(row => row.ExternalId == "root") ?? enabled.FirstOrDefault();
            if (library is null)
            {
                continue;
            }

            var nfo = FindNfo(path);
            var xml = nfo is null ? null : TryLoadXml(nfo);
            var isEpisode = LooksLikeEpisode(path, xml);
            Guid? seriesId = null;
            string? seriesName = null;
            if (isEpisode)
            {
                var showDir = Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(showDir))
                {
                    if (!seriesByFolder.TryGetValue(showDir, out var series))
                    {
                        series = ReadSeries(showDir, library, connection.Id);
                        seriesByFolder[showDir] = series;
                        items.Add(series);
                    }

                    seriesId = series.Id;
                    seriesName = series.Name;
                }
            }

            items.Add(ReadVideo(path, xml, library, connection.Id, isEpisode, seriesId, seriesName));
            if (items.Count == 1 || items.Count % 50 == 0)
            {
                _progress.Fetching(library.Name, 1, Math.Max(enabled.Count, 1), items.Count, 0);
            }
        }

        return Task.FromResult<IReadOnlyList<CatalogItemDto>>(items);
    }

    private static MediaServerLibrary? MatchLibrary(IReadOnlyList<MediaServerLibrary> enabled, string top)
        => enabled.FirstOrDefault(library =>
            library.ExternalId.Equals(top, StringComparison.OrdinalIgnoreCase)
            || library.Name.Equals(top, StringComparison.OrdinalIgnoreCase));

    private static CatalogItemDto ReadSeries(string showDir, MediaServerLibrary library, Guid connectionId)
    {
        var nfo = Path.Combine(showDir, "tvshow.nfo");
        var xml = File.Exists(nfo) ? TryLoadXml(nfo) : null;
        var title = ReadText(xml, "title", "showtitle") ?? Path.GetFileName(showDir);
        return new CatalogItemDto
        {
            Id = StableId(connectionId, showDir),
            Name = title,
            Overview = ReadText(xml, "plot", "outline"),
            Kind = BaseItemKind.Series,
            Path = showDir,
            LibraryId = library.Id,
            LibraryName = library.Name,
            CollectionType = library.CollectionType ?? "tvshows",
            SourceConnectionId = connectionId,
            ProductionYear = ReadInt(xml, "year"),
            PremiereDate = ReadDate(xml, "premiered", "aired"),
            OfficialRating = ReadText(xml, "mpaa"),
            Genres = ReadMany(xml, "genre"),
            Studios = ReadMany(xml, "studio"),
            ProviderIds = ReadIds(xml)
        };
    }

    private static CatalogItemDto ReadVideo(
        string path,
        XDocument? xml,
        MediaServerLibrary library,
        Guid connectionId,
        bool isEpisode,
        Guid? seriesId,
        string? seriesName)
    {
        var title = ReadText(xml, "title") ?? Path.GetFileNameWithoutExtension(path);
        var runtimeMinutes = ReadInt(xml, "runtime");
        return new CatalogItemDto
        {
            Id = StableId(connectionId, path),
            Name = title,
            Overview = ReadText(xml, "plot", "outline"),
            Kind = isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie,
            Path = path,
            SeriesId = seriesId,
            SeriesName = seriesName ?? ReadText(xml, "showtitle"),
            ProductionYear = ReadInt(xml, "year"),
            PremiereDate = ReadDate(xml, "premiered", "aired"),
            OfficialRating = ReadText(xml, "mpaa"),
            CommunityRating = ReadFloat(xml, "rating"),
            RuntimeTicks = runtimeMinutes is > 0 ? TimeSpan.FromMinutes(runtimeMinutes.Value).Ticks : null,
            IndexNumber = ReadInt(xml, "episode"),
            ParentIndexNumber = ReadInt(xml, "season"),
            LibraryId = library.Id,
            LibraryName = library.Name,
            CollectionType = library.CollectionType,
            SourceConnectionId = connectionId,
            Genres = ReadMany(xml, "genre"),
            Studios = ReadMany(xml, "studio"),
            Tags = ReadMany(xml, "tag"),
            Stars = ReadActors(xml),
            ProviderIds = ReadIds(xml)
        };
    }

    private static bool LooksLikeEpisode(string path, XDocument? xml)
    {
        if (xml?.Root?.Name.LocalName.Equals("episodedetails", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("S0", StringComparison.OrdinalIgnoreCase)
            || name.Contains("E0", StringComparison.OrdinalIgnoreCase)
            || name.Contains("x0", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindNfo(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var candidate in new[]
        {
            Path.Combine(dir, stem + ".nfo"),
            Path.Combine(dir, "movie.nfo"),
            Path.Combine(dir, stem + ".xml")
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static XDocument? TryLoadXml(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadText(XDocument? xml, params string[] names)
    {
        if (xml?.Root is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var value = xml.Root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadInt(XDocument? xml, string name)
    {
        var text = ReadText(xml, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static float? ReadFloat(XDocument? xml, string name)
    {
        var text = ReadText(xml, name);
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static DateTime? ReadDate(XDocument? xml, params string[] names)
    {
        var text = ReadText(xml, names);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
    }

    private static List<string>? ReadMany(XDocument? xml, string name)
    {
        if (xml?.Root is null)
        {
            return null;
        }

        var values = xml.Root.Elements()
            .Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();
        return values.Count == 0 ? null : values;
    }

    private static List<string>? ReadActors(XDocument? xml)
    {
        if (xml?.Root is null)
        {
            return null;
        }

        var names = xml.Root.Elements()
            .Where(e => e.Name.LocalName.Equals("actor", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Elements().FirstOrDefault(n => n.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();
        return names.Count == 0 ? null : names;
    }

    private static Dictionary<string, string>? ReadIds(XDocument? xml)
    {
        if (xml?.Root is null)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var imdb = ReadText(xml, "id", "imdbid", "imdb");
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            map["imdb"] = imdb;
        }

        var tmdb = ReadText(xml, "tmdbid", "tmdb");
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            map["tmdb"] = tmdb;
        }

        var tvdb = ReadText(xml, "tvdbid", "tvdb");
        if (!string.IsNullOrWhiteSpace(tvdb))
        {
            map["tvdb"] = tvdb;
        }

        return map.Count == 0 ? null : map;
    }

    private static Guid StableId(Guid connectionId, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId.ToString("N") + "|" + path.Replace('\\', '/')));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string GuessCollectionType(string name)
        => JellyfinLibraryNaming.GuessCollectionType(name) ?? "mixed";
}
