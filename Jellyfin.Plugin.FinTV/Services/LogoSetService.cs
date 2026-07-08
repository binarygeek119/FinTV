using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class LogoSetService
{
    public const string Binarygeek119GitHubRef = "fintv2";

    public const string Binarygeek119GitHubTreeUrl =
        "https://api.github.com/repos/binarygeek119/open-channel-logos/git/trees/fintv2?recursive=1";

    public const string Binarygeek119GitHubRawBase =
        "https://raw.githubusercontent.com/binarygeek119/open-channel-logos/fintv2/";

    private static readonly string[] Binarygeek119LogoPathPrefixes =
    [
        "EBS/",
        "Movies/",
        "News/",
        "Shows/",
        "Music Videos Channels/",
        "The Holiday Channel/",
        "Weather/"
    ];

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
        var isNew = existing is null;

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        Directory.CreateDirectory(plugin.LogosFolder);
        var storagePath = Path.Combine(plugin.LogosFolder, "binarygeek119");
        Directory.CreateDirectory(storagePath);

        existing ??= new LogoSet
        {
            Name = setName,
            SourceUrl = Binarygeek119GitHubRawBase,
            StoragePath = storagePath
        };

        if (isNew)
        {
            _db.LogoSets.Add(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var localCount = Directory.Exists(storagePath)
            ? Directory.EnumerateFiles(storagePath, "*.*", SearchOption.AllDirectories).Count(IsImageFile)
            : 0;

        if (localCount == 0)
        {
            localCount = SeedBundledLogos(storagePath);
        }

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
        if (Directory.Exists(storagePath))
        {
            Directory.Delete(storagePath, true);
        }

        Directory.CreateDirectory(storagePath);
        await DownloadBinarygeek119SetFromGitHubAsync(storagePath, cancellationToken);

        const string setName = ChannelPresets.Binarygeek119LogoSetName;
        var set = await _db.LogoSets.FirstOrDefaultAsync(s => s.Name == setName, cancellationToken);
        if (set is null)
        {
            set = new LogoSet
            {
                Name = setName,
                SourceUrl = Binarygeek119GitHubRawBase,
                StoragePath = storagePath
            };
            _db.LogoSets.Add(set);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            set.SourceUrl = Binarygeek119GitHubRawBase;
            set.StoragePath = storagePath;
        }

        await ScanLocalLogoFolderAsync(set, storagePath, cancellationToken);
        set.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await RepairChannelLogosAsync(set, cancellationToken);
        return set;
    }

    private static int SeedBundledLogos(string storagePath)
    {
        var plugin = Plugin.Instance;
        var bundledPath = plugin?.BundledLogosFolder;
        if (string.IsNullOrWhiteSpace(bundledPath) || !Directory.Exists(bundledPath))
        {
            return 0;
        }

        foreach (var source in Directory.EnumerateFiles(bundledPath, "*.*", SearchOption.AllDirectories).Where(IsImageFile))
        {
            var relative = Path.GetRelativePath(bundledPath, source);
            var destination = Path.Combine(storagePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                continue;
            }

            File.Copy(source, destination);
        }

        return Directory.Exists(storagePath)
            ? Directory.EnumerateFiles(storagePath, "*.*", SearchOption.AllDirectories).Count(IsImageFile)
            : 0;
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

        if (set.Id == Guid.Empty)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        await _db.LogoSetEntries
            .Where(e => e.LogoSetId == set.Id)
            .ExecuteDeleteAsync(cancellationToken);

        DetachTrackedLogoSetEntries(set.Id);
        set.Entries.Clear();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var entry = new LogoSetEntry
            {
                LogoSetId = set.Id,
                FileName = Path.GetFileName(file),
                RelativePath = relative,
                DisplayName = Path.GetFileNameWithoutExtension(file)
            };
            set.Entries.Add(entry);
            _db.LogoSetEntries.Add(entry);
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Indexed {Count} logos for set {Set}", files.Count, set.Name);
    }

    private void DetachTrackedLogoSetEntries(Guid logoSetId)
    {
        foreach (var tracked in _db.ChangeTracker.Entries<LogoSetEntry>()
            .Where(entry => entry.Entity.LogoSetId == logoSetId)
            .ToList())
        {
            tracked.State = EntityState.Detached;
        }
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
        var entry = FindEntry(logoSet, relativePath, channelName)
            ?? FindEntryByName(logoSet, channelName);
        if (entry is null)
        {
            return;
        }

        channel.LogoSetId = logoSet.Id;
        channel.LogoFileName = entry.FileName;
        channel.ChannelLogoPath = ResolveLogoPath(logoSet, entry.RelativePath);
        channel.BugPlacement = BugPlacementMode.Auto;
    }

    public bool TryBindChannelLogo(Channel channel, LogoSet logoSet)
    {
        LogoSetEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(channel.LogoFileName))
        {
            entry = logoSet.Entries.FirstOrDefault(e =>
                e.FileName.Equals(channel.LogoFileName, StringComparison.OrdinalIgnoreCase));
        }

        entry ??= FindEntry(logoSet, null, channel.Name);
        if (entry is null)
        {
            return false;
        }

        channel.LogoSetId = logoSet.Id;
        channel.LogoFileName = entry.FileName;
        channel.ChannelLogoPath = ResolveLogoPath(logoSet, entry.RelativePath);
        return !string.IsNullOrWhiteSpace(channel.ChannelLogoPath);
    }

    public async Task<RepairChannelLogosResult> RepairChannelLogosAsync(
        LogoSet? logoSet = null,
        CancellationToken cancellationToken = default)
    {
        logoSet ??= await GetBinarygeek119SetForRepairAsync(cancellationToken);

        var logoSets = await _db.LogoSets
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);
        var logoSetsById = logoSets.ToDictionary(set => set.Id);
        var validLogoSetIds = logoSetsById.Keys.ToHashSet();

        if (!logoSetsById.ContainsKey(logoSet.Id))
        {
            logoSetsById[logoSet.Id] = logoSet;
            validLogoSetIds.Add(logoSet.Id);
        }

        var binarygeek119Set = logoSetsById[logoSet.Id];
        var channels = await _db.Channels.ToListAsync(cancellationToken);
        var result = new RepairChannelLogosResult { TotalChannels = channels.Count };

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SanitizeChannelLogoReference(channel, validLogoSetIds);

            var hadLogo = HasResolvedLogo(channel);
            var preset = FindPresetForChannel(channel);

            if (preset is not null && preset.UseBinarygeek119Logo)
            {
                ApplyLogoToChannel(channel, binarygeek119Set, preset.LogoRelativePath, preset.Name);
            }
            else if (!channel.LogoSetId.HasValue || string.IsNullOrWhiteSpace(channel.LogoFileName))
            {
                TryBindChannelLogo(channel, binarygeek119Set);
            }

            if (channel.LogoSetId.HasValue && string.IsNullOrWhiteSpace(channel.ChannelLogoPath))
            {
                if (logoSetsById.TryGetValue(channel.LogoSetId.Value, out var assignedSet))
                {
                    TryBindChannelLogo(channel, assignedSet);
                }
                else
                {
                    ClearChannelLogo(channel);
                }
            }

            if (HasResolvedLogo(channel) && !hadLogo)
            {
                result.Repaired.Add(channel.Name);
            }
            else if (!HasResolvedLogo(channel))
            {
                result.Missing.Add(channel.Name);
            }
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to save channel logo repairs");
            throw new InvalidOperationException("Failed to save channel logo assignments.", ex);
        }

        return result;
    }

    private async Task<LogoSet> GetBinarygeek119SetForRepairAsync(CancellationToken cancellationToken)
    {
        const string setName = ChannelPresets.Binarygeek119LogoSetName;
        var existing = await _db.LogoSets
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Name == setName, cancellationToken);

        if (existing is not null && existing.Entries.Count > 0)
        {
            return existing;
        }

        return await EnsureBinarygeek119SetAsync(cancellationToken);
    }

    private static bool HasResolvedLogo(Channel channel)
        => channel.LogoSetId.HasValue
            && !string.IsNullOrWhiteSpace(channel.LogoFileName)
            && !string.IsNullOrWhiteSpace(channel.ChannelLogoPath);

    private static void SanitizeChannelLogoReference(Channel channel, IReadOnlySet<Guid> validLogoSetIds)
    {
        if (channel.LogoSetId.HasValue && !validLogoSetIds.Contains(channel.LogoSetId.Value))
        {
            ClearChannelLogo(channel);
        }
    }

    private static void ClearChannelLogo(Channel channel)
    {
        channel.LogoSetId = null;
        channel.LogoFileName = null;
        channel.ChannelLogoPath = null;
    }

    internal static ChannelPresetDefinition? FindPresetForChannel(Channel channel)
    {
        var byLegacy = ChannelPresets.All.FirstOrDefault(p => p.LegacyNumber == channel.Number);
        if (byLegacy is not null)
        {
            return byLegacy;
        }

        var bySubchannel = ChannelPresets.All.FirstOrDefault(p => p.SubchannelNumber == channel.Number);
        if (bySubchannel is not null)
        {
            return bySubchannel;
        }

        var normalizedName = NormalizeLogoName(channel.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var exactName = ChannelPresets.All.FirstOrDefault(p => NormalizeLogoName(p.Name) == normalizedName);
        if (exactName is not null)
        {
            return exactName;
        }

        if (channel.ContentType == ChannelContentType.Weather)
        {
            return ChannelPresets.All.FirstOrDefault(p => p.IsWeatherChannel);
        }

        if (normalizedName.Contains("newsweather", StringComparison.Ordinal)
            || normalizedName.Contains("newandweather", StringComparison.Ordinal))
        {
            return ChannelPresets.All.FirstOrDefault(p => p.Id == "fintv-weatherstar4000")
                ?? ChannelPresets.All.FirstOrDefault(p => p.Id == "fintv-news");
        }

        return ChannelPresets.All.FirstOrDefault(p =>
            normalizedName.Contains(NormalizeLogoName(p.Name), StringComparison.Ordinal)
            || NormalizeLogoName(p.Name).Contains(normalizedName, StringComparison.Ordinal));
    }

    public static bool IsCustomSet(LogoSet set)
        => string.Equals(set.SourceUrl, "custom", StringComparison.OrdinalIgnoreCase);

    public async Task<LogoSet?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.LogoSets
            .Include(s => s.Entries)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<LogoSet> CreateCustomSetAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Logo set name is required.");
        }

        if (await _db.LogoSets.AnyAsync(s => s.Name == trimmed, cancellationToken))
        {
            throw new InvalidOperationException($"A logo set named \"{trimmed}\" already exists.");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        Directory.CreateDirectory(plugin.LogosFolder);

        var folderName = SanitizeFolderName(trimmed);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = Guid.NewGuid().ToString("N")[..8];
        }

        var storagePath = Path.Combine(plugin.LogosFolder, "custom", folderName);
        var suffix = 1;
        while (Directory.Exists(storagePath) || await _db.LogoSets.AnyAsync(s => s.StoragePath == storagePath, cancellationToken))
        {
            storagePath = Path.Combine(plugin.LogosFolder, "custom", $"{folderName}-{suffix++}");
        }

        Directory.CreateDirectory(storagePath);

        var set = new LogoSet
        {
            Name = trimmed,
            SourceUrl = "custom",
            StoragePath = storagePath,
            LastSyncedAt = DateTime.UtcNow
        };

        _db.LogoSets.Add(set);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Created custom logo set {Name} at {Path}", trimmed, storagePath);
        return set;
    }

    public async Task<LogoSetEntry> UploadCustomLogoAsync(
        Guid setId,
        Stream content,
        string originalFileName,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo set not found.");

        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("Only custom logo sets support uploads.");
        }

        if (!IsImageFile(originalFileName))
        {
            throw new InvalidOperationException("Logo must be a PNG, JPG, JPEG, or WEBP image.");
        }

        var trimmedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            trimmedName = Path.GetFileNameWithoutExtension(originalFileName);
        }

        Directory.CreateDirectory(set.StoragePath);
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var fileName = BuildUniqueFileName(set, trimmedName, extension);
        var relativePath = fileName;
        var destination = Path.Combine(set.StoragePath, fileName);

        await using (var output = File.Create(destination))
        {
            await content.CopyToAsync(output, cancellationToken);
        }

        var entry = new LogoSetEntry
        {
            LogoSetId = set.Id,
            FileName = fileName,
            RelativePath = relativePath,
            DisplayName = trimmedName
        };

        set.Entries.Add(entry);
        _db.LogoSetEntries.Add(entry);
        set.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task<LogoSetEntry> UpdateCustomLogoAsync(
        Guid setId,
        Guid entryId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.FirstOrDefaultAsync(s => s.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo set not found.");

        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("Only custom logo sets can be edited.");
        }

        var entry = await _db.LogoSetEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.LogoSetId == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo not found.");

        var trimmedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Logo name is required.");
        }

        entry.DisplayName = trimmedName;
        set.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task DeleteCustomLogoAsync(Guid setId, Guid entryId, CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.FirstOrDefaultAsync(s => s.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo set not found.");

        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("Only custom logo sets can be edited.");
        }

        var entry = await _db.LogoSetEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.LogoSetId == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo not found.");

        var path = ResolveLogoPath(set, entry.RelativePath);
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }

        _db.LogoSetEntries.Remove(entry);
        set.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCustomSetAsync(Guid setId, CancellationToken cancellationToken = default)
    {
        var set = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Id == setId, cancellationToken)
            ?? throw new InvalidOperationException("Logo set not found.");

        if (!IsCustomSet(set))
        {
            throw new InvalidOperationException("Only custom logo sets can be deleted.");
        }

        var inUse = await _db.Channels.AnyAsync(c => c.LogoSetId == setId, cancellationToken);
        if (inUse)
        {
            throw new InvalidOperationException("This logo set is assigned to one or more channels. Clear those assignments first.");
        }

        if (Directory.Exists(set.StoragePath))
        {
            Directory.Delete(set.StoragePath, true);
        }

        _db.LogoSetEntries.RemoveRange(set.Entries);
        _db.LogoSets.Remove(set);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public string? ResolveLogoPath(LogoSet set, string relativePath)
    {
        var path = Path.Combine(set.StoragePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    private static string BuildUniqueFileName(LogoSet set, string displayName, string extension)
    {
        var baseName = SanitizeFileName(displayName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "logo";
        }

        var fileName = baseName + extension;
        var counter = 1;
        while (set.Entries.Any(entry => entry.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            || File.Exists(Path.Combine(set.StoragePath, fileName)))
        {
            fileName = $"{baseName}-{counter++}{extension}";
        }

        return fileName;
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Trim()
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim('-', ' ');

        return cleaned.Replace(" ", "-");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim('_', ' ', '.');

        return cleaned.Replace(" ", "_");
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
                && IsBundledLogoPath(item.Path)
                && IsImageFile(item.Path))
            .ToList();

        _logger.LogInformation("Downloading {Count} Binarygeek119 logos from GitHub ({Ref})", files.Count, Binarygeek119GitHubRef);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.Path;
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

    private static bool IsBundledLogoPath(string path)
        => Binarygeek119LogoPathPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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
