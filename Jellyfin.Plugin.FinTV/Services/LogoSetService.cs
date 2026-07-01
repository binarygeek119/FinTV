using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class LogoSetService
{
    public const string Binarygeek119GitHubTreeUrl =
        "https://api.github.com/repos/binarygeek119/open-channel-logos/git/trees/master?recursive=1";

    public const string Binarygeek119GitHubRawBase =
        "https://raw.githubusercontent.com/binarygeek119/open-channel-logos/master/";

    public const string Binarygeek119EnglishPrefix = "Binarygeek119 Set/English/";

    private readonly FinTvDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LogoSetService> _logger;

    public LogoSetService(FinTvDbContext db, IHttpClientFactory httpClientFactory, ILogger<LogoSetService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<LogoSet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.LogoSets
            .Include(s => s.Entries)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<LogoSet> EnsureBinarygeek119SetAsync(CancellationToken cancellationToken = default)
    {
        const string setName = ChannelPresets.Binarygeek119LogoSetName;
        var existing = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Name == setName, cancellationToken);

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        Directory.CreateDirectory(plugin.LogosFolder);
        var storagePath = Path.Combine(plugin.LogosFolder, "binarygeek119");
        Directory.CreateDirectory(storagePath);

        existing ??= new LogoSet
        {
            Name = setName,
            SourceUrl = Binarygeek119GitHubRawBase + Binarygeek119EnglishPrefix,
            StoragePath = storagePath
        };

        if (existing.Id == Guid.Empty)
        {
            _db.LogoSets.Add(existing);
        }

        var localCount = Directory.Exists(storagePath)
            ? Directory.EnumerateFiles(storagePath, "*.*", SearchOption.AllDirectories).Count(IsImageFile)
            : 0;

        if (localCount == 0)
        {
            await DownloadBinarygeek119SetFromGitHubAsync(storagePath, cancellationToken);
        }

        await ScanLocalLogoFolderAsync(existing, storagePath, cancellationToken);
        existing.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<LogoSet> SyncBinarygeek119FromGitHubAsync(CancellationToken cancellationToken = default)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var storagePath = Path.Combine(plugin.LogosFolder, "binarygeek119");
        Directory.CreateDirectory(storagePath);
        await DownloadBinarygeek119SetFromGitHubAsync(storagePath, cancellationToken);
        return await EnsureBinarygeek119SetAsync(cancellationToken);
    }

    public async Task ScanLocalLogoFolderAsync(LogoSet set, string folder, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(IsImageFile)
            .ToList();

        _db.LogoSetEntries.RemoveRange(set.Entries);
        set.Entries.Clear();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            set.Entries.Add(new LogoSetEntry
            {
                LogoSetId = set.Id,
                FileName = Path.GetFileName(file),
                RelativePath = relative,
                DisplayName = Path.GetFileNameWithoutExtension(file)
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Indexed {Count} logos for set {Set}", files.Count, set.Name);
    }

    public string? ResolveLogoPath(LogoSet set, string relativePath)
    {
        var path = Path.Combine(set.StoragePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    public LogoSetEntry? FindEntry(LogoSet set, string? relativePath, string? channelName = null)
    {
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var match = set.Entries.FirstOrDefault(entry =>
                entry.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)
                || entry.RelativePath.EndsWith('/' + relativePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        return FindEntryByName(set, channelName);
    }

    internal static LogoSetEntry? FindEntryByName(LogoSet set, string channelName)
    {
        var target = NormalizeLogoName(channelName);
        var exact = set.Entries.FirstOrDefault(entry =>
            NormalizeLogoName(Path.GetFileNameWithoutExtension(entry.FileName)) == target
            || NormalizeLogoName(entry.DisplayName ?? string.Empty) == target);
        if (exact is not null)
        {
            return exact;
        }

        return set.Entries.FirstOrDefault(entry =>
            NormalizeLogoName(Path.GetFileNameWithoutExtension(entry.FileName)).Contains(target, StringComparison.Ordinal)
            || NormalizeLogoName(entry.DisplayName ?? string.Empty).Contains(target, StringComparison.Ordinal));
    }

    private static string NormalizeLogoName(string value)
        => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public void ApplyLogoToChannel(Channel channel, LogoSet logoSet, string? relativePath, string channelName)
    {
        var entry = FindEntry(logoSet, relativePath, channelName);
        if (entry is null)
        {
            return;
        }

        channel.LogoSetId = logoSet.Id;
        channel.LogoFileName = entry.FileName;
        channel.ChannelLogoPath = ResolveLogoPath(logoSet, entry.RelativePath);
        channel.BugPlacement = BugPlacementMode.Auto;
    }

    private async Task DownloadBinarygeek119SetFromGitHubAsync(string storagePath, CancellationToken cancellationToken)
    {
        var client = CreateGitHubClient();
        var tree = await client.GetFromJsonAsync<GitHubTreeResponse>(Binarygeek119GitHubTreeUrl, cancellationToken);
        if (tree?.Tree is null)
        {
            _logger.LogWarning("Unable to read Binarygeek119 logo tree from GitHub");
            return;
        }

        var files = tree.Tree
            .Where(item =>
                item.Type == "blob"
                && item.Path.StartsWith(Binarygeek119EnglishPrefix, StringComparison.OrdinalIgnoreCase)
                && IsImageFile(item.Path))
            .ToList();

        _logger.LogInformation("Downloading {Count} Binarygeek119 logos from GitHub", files.Count);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.Path[Binarygeek119EnglishPrefix.Length..];
            var destination = Path.Combine(storagePath, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                continue;
            }

            try
            {
                var url = ToRawGitHubUrl(file.Path);
                using var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to download logo {Path}: {Status}", file.Path, response.StatusCode);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(destination);
                await stream.CopyToAsync(output, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download logo {Path}", file.Path);
            }
        }
    }

    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FinTV-Jellyfin-Plugin");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string ToRawGitHubUrl(string repoPath)
        => Binarygeek119GitHubRawBase + string.Join("/", repoPath.Split('/').Select(Uri.EscapeDataString));

    private static bool IsImageFile(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    private sealed class GitHubTreeResponse
    {
        [JsonPropertyName("tree")]
        public List<GitHubTreeItem>? Tree { get; set; }
    }

    private sealed class GitHubTreeItem
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}
