using System.IO.Compression;
using System.Net.Http;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class LogoSetService
{
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
        const string setName = "Binarygeek119 Set";
        var existing = await _db.LogoSets.Include(s => s.Entries).FirstOrDefaultAsync(s => s.Name == setName, cancellationToken);
        if (existing is not null && existing.Entries.Count > 0)
        {
            return existing;
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        Directory.CreateDirectory(plugin.LogosFolder);
        var storagePath = Path.Combine(plugin.LogosFolder, "binarygeek119");
        Directory.CreateDirectory(storagePath);

        existing ??= new LogoSet
        {
            Name = setName,
            SourceUrl = plugin.Configuration.Binarygeek119LogoSetUrl,
            StoragePath = storagePath
        };

        if (existing.Id == Guid.Empty)
        {
            _db.LogoSets.Add(existing);
        }

        await ScanLocalLogoFolderAsync(existing, storagePath, cancellationToken);
        existing.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task ScanLocalLogoFolderAsync(LogoSet set, string folder, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _db.LogoSetEntries.RemoveRange(set.Entries);
        set.Entries.Clear();

        foreach (var file in files)
        {
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
}
