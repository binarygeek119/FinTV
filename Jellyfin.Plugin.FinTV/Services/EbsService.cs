using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Resolves Emergency Broadcast Slate assets for off-air channels.
/// </summary>
public class EbsService
{
    public const string EbsFolderName = "EBS";

    private static readonly string[] DefaultSlateFiles =
    [
        "offlineusa.jpg",
        "offline.jpg",
        "offline2.jpg"
    ];

    private readonly JellyfinCatalogService _catalog;

    public EbsService(JellyfinCatalogService catalog)
    {
        _catalog = catalog;
    }

    public string? ResolveRandomSlatePath()
    {
        foreach (var root in GetEbsRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsImageFile)
                .ToList();
            if (files.Count > 0)
            {
                return files[Random.Shared.Next(files.Count)];
            }
        }

        foreach (var fileName in DefaultSlateFiles)
        {
            foreach (var root in GetEbsRoots())
            {
                var path = Path.Combine(root, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    public BaseItem? PickBackgroundMusicTrack()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return null;
        }

        IReadOnlyList<BaseItem> tracks = config.EbsBackgroundMusicSource == EbsBackgroundMusicSource.AllMusicLibraries
            ? _catalog.QueryAllMusicAudio()
            : _catalog.QueryMusicAudioFromLibrary(config.EbsBackgroundMusicLibraryId, config.EbsBackgroundMusicLibraryName);

        if (tracks.Count == 0)
        {
            return null;
        }

        return tracks[Random.Shared.Next(tracks.Count)];
    }

    private static IEnumerable<string> GetEbsRoots()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            yield break;
        }

        yield return Path.Combine(plugin.LogosFolder, "binarygeek119", EbsFolderName);
        yield return Path.Combine(plugin.BundledLogosFolder, EbsFolderName);
    }

    private static bool IsImageFile(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
}
