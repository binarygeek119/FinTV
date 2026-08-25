using FinTv.Domain;
using FinTv.Streaming;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Resolves Off Air assets and playback plans when a channel has no scheduled media.
/// </summary>
public class EbsService
{
    public const string EbsFolderName = "EBS";

    public const string OfflineFolderName = "OFFLINE";

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
        var config = FinTvRuntime.Current?.Configuration;
        var displayMode = config?.EbsDisplayMode ?? EbsDisplayMode.SlateImage;
        var audioMode = config?.EbsAudioMode ?? EbsAudioMode.BackgroundMusic;

        string? slatePath = null;
        if (displayMode == EbsDisplayMode.SlateImage)
        {
            slatePath = ResolveSlatePath(channel.AspectRatio);
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
        => ResolveSlatePath(FinTvRuntime.Current?.Configuration.EbsSlateVariant ?? EbsSlateVariant.Usa, AspectRatioMode.SixteenNine);

    public string? ResolveSlatePath(AspectRatioMode aspect)
        => ResolveSlatePath(FinTvRuntime.Current?.Configuration.EbsSlateVariant ?? EbsSlateVariant.Usa, aspect);

    public string? ResolveSlatePath(EbsSlateVariant variant)
        => ResolveSlatePath(variant, AspectRatioMode.SixteenNine);

    public string? ResolveSlatePath(EbsSlateVariant variant, AspectRatioMode aspect)
    {
        var customPath = ResolveCustomSlatePath(variant);
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return customPath;
        }

        return ResolveStockSlatePath(variant, aspect);
    }

    public string? ResolveStockSlatePath(EbsSlateVariant variant)
        => ResolveStockSlatePath(variant, AspectRatioMode.SixteenNine);

    public string? ResolveStockSlatePath(EbsSlateVariant variant, AspectRatioMode aspect)
    {
        foreach (var file in GetPreferredStockFiles(variant, aspect))
        {
            var found = FindStockFile(file);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        var tagged = EnumerateStockImages()
            .Where(path => MatchesVariant(path, variant) && MatchesNamedAspect(path, aspect))
            .ToList();
        if (tagged.Count > 0)
        {
            return tagged[Random.Shared.Next(tagged.Count)];
        }

        var untagged = EnumerateStockImages()
            .Where(path => MatchesVariant(path, variant) && !HasNamedAspect(path))
            .ToList();
        if (untagged.Count > 0)
        {
            return untagged[Random.Shared.Next(untagged.Count)];
        }

        return null;
    }

    public string? ResolveRandomSlatePath() => ResolveSlatePath();

    public string? ResolveCustomSlatePath(EbsSlateVariant variant)
    {
        var folder = FinTvRuntime.Current?.EbsCustomSlatesFolder;
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

        var plugin = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow plugin not initialized.");
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
        var config = FinTvRuntime.Current?.Configuration;
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
        var config = FinTvRuntime.Current?.Configuration;
        if (config is null)
        {
            return _catalog.PickPlayableMusicPath(null, null, fallbackToAllMusic: true);
        }

        return config.EbsBackgroundMusicSource == EbsBackgroundMusicSource.AllMusicLibraries
            ? _catalog.PickPlayableMusicPath(null, null, fallbackToAllMusic: true)
            : _catalog.PickPlayableMusicPath(
                config.EbsBackgroundMusicLibraryId,
                config.EbsBackgroundMusicLibraryName,
                fallbackToAllMusic: true);
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
        var folder = FinTvRuntime.Current?.EbsCustomSlatesFolder;
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

    private static IEnumerable<string> GetPreferredStockFiles(EbsSlateVariant variant, AspectRatioMode aspect)
    {
        var fourThree = aspect == AspectRatioMode.FourThree;
        if (variant == EbsSlateVariant.Usa)
        {
            yield return fourThree ? "offline_usa_4_3.jpg" : "offline_usa_16_9.jpg";
            yield return UsaSlateFile;
            yield break;
        }

        yield return fourThree ? "offline_world_4_3.jpg" : "offline_world_16_9.jpg";
        yield return InternationalSlateFile;
    }

    private static string? FindStockFile(string fileName)
    {
        foreach (var root in GetStockSlateRoots())
        {
            var path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateStockImages()
    {
        foreach (var root in GetStockSlateRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (IsImageFile(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> GetStockSlateRoots()
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            yield break;
        }

        yield return Path.Combine(plugin.LogosFolder, "binarygeek119", EbsFolderName);
        yield return Path.Combine(plugin.LogosFolder, "binarygeek119", OfflineFolderName);
        yield return Path.Combine(plugin.BundledLogosFolder, EbsFolderName);
        yield return Path.Combine(plugin.BundledLogosFolder, OfflineFolderName);
    }

    private static bool MatchesVariant(string path, EbsSlateVariant variant)
    {
        var name = Path.GetFileName(path);
        var isUsa = name.Contains("usa", StringComparison.OrdinalIgnoreCase);
        return variant == EbsSlateVariant.Usa ? isUsa : !isUsa;
    }

    private static bool HasNamedAspect(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return ContainsToken(name, "4_3")
            || ContainsToken(name, "4-3")
            || ContainsToken(name, "16_9")
            || ContainsToken(name, "16-9");
    }

    private static bool MatchesNamedAspect(string path, AspectRatioMode aspect)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var isFourThree = ContainsToken(name, "4_3") || ContainsToken(name, "4-3");
        var isSixteenNine = ContainsToken(name, "16_9") || ContainsToken(name, "16-9");
        if (aspect == AspectRatioMode.FourThree)
        {
            return isFourThree;
        }

        return isSixteenNine;
    }

    private static bool ContainsToken(string name, string token)
        => name.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool IsImageFile(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
}
