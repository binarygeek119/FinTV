using System.Security.Cryptography;
using System.Text;
using FinTv;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public sealed class PathRemapService
{
    private readonly FinTvDbContext _db;
    private IReadOnlyList<PathMapping>? _mappings;

    public PathRemapService(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PathMapping>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _db.PathMappings.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(cancellationToken);

    public async Task ReplaceAllAsync(IReadOnlyList<PathMapping> mappings, CancellationToken cancellationToken = default)
    {
        var existing = await _db.PathMappings.ToListAsync(cancellationToken);
        _db.PathMappings.RemoveRange(existing);
        var order = 0;
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.JellyfinPrefix) || string.IsNullOrWhiteSpace(mapping.LocalPrefix))
            {
                continue;
            }

            _db.PathMappings.Add(new PathMapping
            {
                JellyfinPrefix = NormalizePrefix(mapping.JellyfinPrefix),
                LocalPrefix = NormalizePrefix(mapping.LocalPrefix),
                IgnoreCase = mapping.IgnoreCase,
                SortOrder = order++
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _mappings = null;
    }

    public string? Remap(string? jellyfinPath, IReadOnlyList<PathMapping>? mappings = null)
    {
        if (string.IsNullOrWhiteSpace(jellyfinPath))
        {
            return jellyfinPath;
        }

        var source = jellyfinPath.Replace('\\', '/');
        mappings ??= LoadMappings();
        PathMapping? best = null;
        foreach (var mapping in mappings)
        {
            var prefix = mapping.JellyfinPrefix.Replace('\\', '/').TrimEnd('/');
            var comparison = mapping.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (source.StartsWith(prefix, comparison)
                && (best is null || prefix.Length > best.JellyfinPrefix.Replace('\\', '/').TrimEnd('/').Length))
            {
                best = mapping;
            }
        }

        if (best is null)
        {
            return source;
        }

        var from = best.JellyfinPrefix.Replace('\\', '/').TrimEnd('/');
        var to = best.LocalPrefix.Replace('\\', '/').TrimEnd('/');
        var rest = source[from.Length..];
        if (!rest.StartsWith('/') && rest.Length > 0)
        {
            rest = "/" + rest;
        }

        return to + rest;
    }

    public string? ResolveExistingPath(string? jellyfinPath)
    {
        var remapped = Remap(jellyfinPath);
        if (LocalPathExists(remapped))
        {
            return remapped;
        }

        if (LocalPathExists(jellyfinPath))
        {
            return jellyfinPath;
        }

        return remapped;
    }

    /// <summary>
    /// True when the Jellyfin path, after remap, exists as a local file or folder.
    /// </summary>
    public bool ExistsAtRemappedPath(string? jellyfinPath, IReadOnlyList<PathMapping>? mappings = null)
    {
        var remapped = Remap(jellyfinPath, mappings);
        if (LocalPathExists(remapped))
        {
            return true;
        }

        return LocalPathExists(jellyfinPath);
    }

    public static bool LocalPathExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    public async Task<object> TestAsync(int sampleSize, CancellationToken cancellationToken = default)
    {
        var mappings = await GetAllAsync(cancellationToken);
        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => i.Path != null && i.Path != "")
            .OrderBy(i => i.Name)
            .Take(Math.Clamp(sampleSize, 1, 500))
            .Select(i => new { i.Id, i.Name, i.Path })
            .ToListAsync(cancellationToken);

        var exists = 0;
        var missing = 0;
        var samples = new List<object>();
        foreach (var item in items)
        {
            var local = Remap(item.Path, mappings);
            var found = ExistsAtRemappedPath(item.Path, mappings);
            if (found)
            {
                exists++;
            }
            else
            {
                missing++;
            }

            if (samples.Count < 15)
            {
                samples.Add(new { item.Id, item.Name, jellyfinPath = item.Path, localPath = local, exists = found });
            }
        }

        return new { total = items.Count, exists, missing, mappings = mappings.Count, samples };
    }

    public IReadOnlyList<PathMapping> LoadMappings()
        => _mappings ??= _db.PathMappings.AsNoTracking().OrderBy(m => m.SortOrder).ToList();

    private static string NormalizePrefix(string prefix) => prefix.Trim().Replace('\\', '/').TrimEnd('/');
}

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2")
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public sealed class FfmpegLocator : IFfmpegLocator
{
    public string EncoderPath { get; }

    public FfmpegLocator(IConfiguration configuration)
    {
        EncoderPath = Resolve(FirstNonBlank(
            configuration["FFMPEG_PATH"],
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            AppEnvironment.Get("FFMPEG_PATH")));
    }

    private static string Resolve(string? configured)
    {
        foreach (var candidate in EnumerateCandidates(configured))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.IsNullOrWhiteSpace(configured) ? "ffmpeg" : configured.Trim();
    }

    private static IEnumerable<string> EnumerateCandidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Trim();
        }

        yield return "/usr/lib/jellyfin-ffmpeg/ffmpeg";

        var onPath = FindOnPath("ffmpeg");
        if (!string.IsNullOrWhiteSpace(onPath))
        {
            yield return onPath;
        }

        foreach (var wellKnown in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" })
        {
            yield return wellKnown;
        }
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }

        return null;
    }
}

public sealed class PublicBaseUrl : IPublicBaseUrl
{
    public string GetLoopbackHttpAddress()
        => AppEnvironment.Get("PUBLIC_URL")
           ?? "http://127.0.0.1:8097";

    public string GetSmartApiUrl(HttpRequest request)
    {
        var configured = ReverseProxyHosting.NormalizePublicBaseUrl(FinTvRuntime.Current?.Configuration.PublicBaseUrl);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromEnv = ReverseProxyHosting.NormalizePublicBaseUrl(AppEnvironment.Get("PUBLIC_URL"));
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim().TrimEnd('/');
        }

        return ReverseProxyHosting.PublicOrigin(request);
    }
}

public sealed class ApiKeyOptions
{
    public string ApiKey { get; set; } = string.Empty;
}
