using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Resolves Emergency Broadcast System assets and playback plans for off-air channels.
/// </summary>
public class EbsService
{
    public const string EbsFolderName = "EBS";

    private static readonly string UsaSlateFile = "offlineusa.jpg";

    private static readonly string InternationalSlateFile = "offline.jpg";

    private static readonly HashSet<string> AllowedUploadExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    private readonly JellyfinCatalogService _catalog;
    private readonly ILogger<EbsService> _logger;

    public EbsService(JellyfinCatalogService catalog, ILogger<EbsService> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public EbsPlaybackPlan CreatePlaybackPlan(Channel channel, double durationSeconds)
    {
        var config = Plugin.Instance?.Configuration;
        var displayMode = config?.EbsDisplayMode ?? EbsDisplayMode.SlateImage;
        var audioMode = config?.EbsAudioMode ?? EbsAudioMode.BackgroundMusic;

        string? slatePath = null;
        if (displayMode == EbsDisplayMode.SlateImage)
        {
            slatePath = ResolveSlatePath();
            if (string.IsNullOrWhiteSpace(slatePath))
            {
                _logger.LogWarning(
                    "No EBS slate found for channel {Channel}; falling back to color bars",
                    channel.Name);
                displayMode = EbsDisplayMode.ColorBars;
            }
        }

        string? musicPath = null;
        if (audioMode == EbsAudioMode.BackgroundMusic)
        {
            musicPath = ResolveBackgroundMusicPath();
            if (string.IsNullOrWhiteSpace(musicPath))
            {
                audioMode = EbsAudioMode.Silence;
            }
        }

        return new EbsPlaybackPlan
        {
            DisplayMode = displayMode,
            AudioMode = audioMode,
            SlateImagePath = slatePath,
            MusicPath = musicPath,
            DurationSeconds = durationSeconds
        };
    }

    public string? ResolveSlatePath()
    {
        var variant = Plugin.Instance?.Configuration.EbsSlateVariant ?? EbsSlateVariant.Usa;
        var customPath = ResolveCustomSlatePath(variant);
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return customPath;
        }

        var preferredFile = variant == EbsSlateVariant.Usa ? UsaSlateFile : InternationalSlateFile;
        foreach (var root in GetStockSlateRoots())
        {
            var path = Path.Combine(root, preferredFile);
            if (File.Exists(path))
            {
                return path;
            }
        }

        foreach (var root in GetStockSlateRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => IsImageFile(path) && MatchesVariant(path, variant))
                .ToList();
            if (files.Count > 0)
            {
                return files[Random.Shared.Next(files.Count)];
            }
        }

        return null;
    }

    public string? ResolveRandomSlatePath() => ResolveSlatePath();

    public string? ResolveCustomSlatePath(EbsSlateVariant variant)
    {
        var folder = Plugin.Instance?.EbsCustomSlatesFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var prefix = GetCustomSlatePrefix(variant);
        return Directory.EnumerateFiles(folder, prefix + ".*", SearchOption.TopDirectoryOnly)
            .Where(path => AllowedUploadExtensions.Contains(Path.GetExtension(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public IReadOnlyDictionary<string, object?> GetCustomSlateStatus()
    {
        return new Dictionary<string, object?>
        {
            ["usa"] = DescribeCustomSlate(EbsSlateVariant.Usa),
            ["international"] = DescribeCustomSlate(EbsSlateVariant.International)
        };
    }

    public async Task UploadCustomSlateAsync(
        EbsSlateVariant variant,
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalFileName);
        if (!AllowedUploadExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Only PNG and JPG images are supported.");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
        Directory.CreateDirectory(plugin.EbsCustomSlatesFolder);
        RemoveCustomSlateFiles(variant);

        var destination = Path.Combine(
            plugin.EbsCustomSlatesFolder,
            GetCustomSlatePrefix(variant) + extension.ToLowerInvariant());

        await using var output = File.Create(destination);
        await content.CopyToAsync(output, cancellationToken);
        _logger.LogInformation("Uploaded custom EBS slate for {Variant} to {Path}", variant, destination);
    }

    public void DeleteCustomSlate(EbsSlateVariant variant)
    {
        RemoveCustomSlateFiles(variant);
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

    public string? ResolveBackgroundMusicPath()
    {
        var track = PickBackgroundMusicTrack();
        return track is null ? null : _catalog.GetMediaPath(track);
    }

    private object? DescribeCustomSlate(EbsSlateVariant variant)
    {
        var path = ResolveCustomSlatePath(variant);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return new
        {
            fileName = Path.GetFileName(path),
            uploadedAt = File.GetLastWriteTimeUtc(path)
        };
    }

    private void RemoveCustomSlateFiles(EbsSlateVariant variant)
    {
        var folder = Plugin.Instance?.EbsCustomSlatesFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var prefix = GetCustomSlatePrefix(variant);
        foreach (var path in Directory.EnumerateFiles(folder, prefix + ".*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }
    }

    private static string GetCustomSlatePrefix(EbsSlateVariant variant)
        => variant == EbsSlateVariant.Usa ? "usa" : "international";

    private static IEnumerable<string> GetStockSlateRoots()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            yield break;
        }

        yield return Path.Combine(plugin.LogosFolder, "binarygeek119", EbsFolderName);
        yield return Path.Combine(plugin.BundledLogosFolder, EbsFolderName);
    }

    private static bool MatchesVariant(string path, EbsSlateVariant variant)
    {
        var name = Path.GetFileName(path);
        var isUsa = name.Contains("usa", StringComparison.OrdinalIgnoreCase);
        return variant == EbsSlateVariant.Usa ? isUsa : !isUsa;
    }

    private static bool IsImageFile(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
}
