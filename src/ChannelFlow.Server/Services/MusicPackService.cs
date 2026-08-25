using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinTv.Domain;
using FinTv.Music;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public sealed class MusicPackService
{
    public const string AnytimeId = "anytime";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".wav", ".wma"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MusicPackService> _logger;
    private readonly ConcurrentDictionary<string, PackJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catalogGate = new();
    private IReadOnlyList<MusicPackDefinition>? _catalog;

    public MusicPackService(IHttpClientFactory httpFactory, ILogger<MusicPackService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public IReadOnlyList<MusicPackStatus> ListPacks()
    {
        var activeId = ResolveActivePackId();
        return LoadCatalog().Select(pack => Describe(pack, activeId)).ToList();
    }

    public string? PickActiveTrackPath()
    {
        var packId = ResolveActivePackId();
        if (string.IsNullOrWhiteSpace(packId))
        {
            return null;
        }

        var tracks = ListTracks(packId);
        return tracks.Count == 0 ? null : tracks[Random.Shared.Next(tracks.Count)];
    }

    public async Task EnsureAnytimeDownloadedAsync(CancellationToken cancellationToken)
    {
        var anytime = LoadCatalog().FirstOrDefault(pack =>
            string.Equals(pack.Id, AnytimeId, StringComparison.OrdinalIgnoreCase));
        if (anytime is null || string.IsNullOrWhiteSpace(anytime.GoogleDriveFileId))
        {
            return;
        }

        if (ListTracks(anytime.Id).Count > 0)
        {
            return;
        }

        _logger.LogInformation("Anytime music pack is missing; downloading v{Version}", anytime.Version);
        await DownloadAsync(anytime.Id, cancellationToken);
    }

    public Task DownloadAsync(string packId, CancellationToken cancellationToken)
    {
        var pack = FindPack(packId)
            ?? throw new InvalidOperationException("Unknown music pack.");
        if (string.IsNullOrWhiteSpace(pack.GoogleDriveFileId))
        {
            throw new InvalidOperationException($"{pack.Name} does not have a Google Drive file yet.");
        }

        var job = _jobs.GetOrAdd(pack.Id, _ => new PackJob());
        lock (job.Gate)
        {
            if (job.Running is { IsCompleted: false })
            {
                return job.Running;
            }

            job.Status = "downloading";
            job.Error = null;
            job.Running = RunDownloadAsync(pack, job, cancellationToken);
            return job.Running;
        }
    }

    public void Delete(string packId)
    {
        var pack = FindPack(packId)
            ?? throw new InvalidOperationException("Unknown music pack.");
        var folder = PackFolder(pack.Id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        _jobs.TryRemove(pack.Id, out _);
        _logger.LogInformation("Removed local music pack {PackId}", pack.Id);
    }

    private async Task RunDownloadAsync(MusicPackDefinition pack, PackJob job, CancellationToken cancellationToken)
    {
        var root = MusicRoot();
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"{pack.Id}_v{MusicPackVersions.Normalize(pack.Version)}.zip");
        var extractDir = PackFolder(pack.Id);
        var staging = extractDir + ".staging";

        try
        {
            await DownloadDriveFileAsync(pack.GoogleDriveFileId!, zipPath, cancellationToken);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            FlattenExtractedFolder(staging);
            RemoveJunk(staging);

            if (ListTracksIn(staging).Count == 0)
            {
                throw new InvalidOperationException($"{pack.Name} zip did not contain any audio files.");
            }

            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            Directory.Move(staging, extractDir);
            WriteInstallRecord(extractDir, pack);
            job.Status = "ready";
            job.Error = null;
            _logger.LogInformation(
                "Installed music pack {PackId} v{Version} ({Tracks} tracks)",
                pack.Id,
                pack.Version,
                ListTracks(pack.Id).Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Status = "error";
            job.Error = ex.Message;
            _logger.LogWarning(ex, "Music pack download failed for {PackId}", pack.Id);
        }
        finally
        {
            TryDelete(zipPath);
            if (Directory.Exists(staging))
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private async Task DownloadDriveFileAsync(string fileId, string destination, CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient(nameof(MusicPackService));
        var url = "https://drive.google.com/uc?export=download&id="
            + Uri.EscapeDataString(fileId)
            + "&confirm=t";
        using var first = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        first.EnsureSuccessStatusCode();

        if (LooksLikeHtml(first))
        {
            var html = await first.Content.ReadAsStringAsync(cancellationToken);
            var confirm = ExtractConfirmToken(html);
            var retryUrl = confirm is not null
                ? "https://drive.google.com/uc?export=download&id="
                    + Uri.EscapeDataString(fileId)
                    + "&confirm="
                    + Uri.EscapeDataString(confirm)
                : "https://drive.usercontent.google.com/download?id="
                    + Uri.EscapeDataString(fileId)
                    + "&export=download&confirm=t";
            using var retry = await client.GetAsync(retryUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            retry.EnsureSuccessStatusCode();
            if (LooksLikeHtml(retry))
            {
                throw new InvalidOperationException("Google Drive returned a web page instead of the zip. Check that the file is shared with anyone who has the link.");
            }

            await WriteFileAsync(retry, destination, cancellationToken);
            await EnsureZipMagicAsync(destination, cancellationToken);
            return;
        }

        await WriteFileAsync(first, destination, cancellationToken);
        await EnsureZipMagicAsync(destination, cancellationToken);
    }

    private static async Task WriteFileAsync(HttpResponseMessage response, string destination, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task EnsureZipMagicAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[4];
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read < 2 || header[0] != (byte)'P' || header[1] != (byte)'K')
        {
            throw new InvalidOperationException("Downloaded file is not a zip. Check the Google Drive share link.");
        }
    }

    private static bool LooksLikeHtml(HttpResponseMessage response)
    {
        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        return media.Contains("html", StringComparison.OrdinalIgnoreCase)
            || media.Contains("text/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractConfirmToken(string html)
    {
        var named = Regex.Match(html, @"name=[""']confirm[""']\s+value=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (named.Success)
        {
            return named.Groups[1].Value;
        }

        var query = Regex.Match(html, @"confirm=([0-9A-Za-z_-]+)", RegexOptions.IgnoreCase);
        return query.Success ? query.Groups[1].Value : null;
    }

    private MusicPackStatus Describe(MusicPackDefinition pack, string? activeId)
    {
        var tracks = ListTracks(pack.Id);
        var installed = ReadInstallRecord(pack.Id);
        _jobs.TryGetValue(pack.Id, out var job);
        var downloading = job is { Status: "downloading" }
            && (job.Running is null || !job.Running.IsCompleted);
        string status;
        if (downloading)
        {
            status = "downloading";
        }
        else if (job is { Status: "error" })
        {
            status = "error";
        }
        else if (string.IsNullOrWhiteSpace(pack.GoogleDriveFileId))
        {
            status = "unavailable";
        }
        else if (tracks.Count == 0)
        {
            status = "idle";
        }
        else if (installed is not null
            && MusicPackVersions.Compare(pack.Version, installed.Version) > 0)
        {
            status = "updateAvailable";
        }
        else
        {
            status = "ready";
        }

        return new MusicPackStatus
        {
            Id = pack.Id,
            Name = pack.Name,
            Season = pack.Season,
            PlaysWhen = DescribeWhen(pack),
            CatalogVersion = MusicPackVersions.Normalize(pack.Version),
            InstalledVersion = installed is null ? null : MusicPackVersions.Normalize(installed.Version),
            TrackCount = tracks.Count,
            Status = status,
            Error = job?.Error,
            HasDriveFile = !string.IsNullOrWhiteSpace(pack.GoogleDriveFileId),
            IsActive = string.Equals(pack.Id, activeId, StringComparison.OrdinalIgnoreCase)
        };
    }

    private string? ResolveActivePackId()
    {
        var date = TodayInScheduleZone();
        var holiday = HolidayChannelCalendar.GetActiveHoliday(date);
        var packs = LoadCatalog();

        if (holiday is not null)
        {
            var holidayPack = packs.FirstOrDefault(pack =>
                pack.Season.Equals(holiday.Id, StringComparison.OrdinalIgnoreCase)
                && ListTracks(pack.Id).Count > 0);
            if (holidayPack is not null)
            {
                return holidayPack.Id;
            }
        }

        if (IsFall(date))
        {
            var fall = packs.FirstOrDefault(pack =>
                pack.Season.Equals("fall", StringComparison.OrdinalIgnoreCase)
                && ListTracks(pack.Id).Count > 0);
            if (fall is not null)
            {
                return fall.Id;
            }
        }

        if (IsWinter(date))
        {
            var winter = packs.FirstOrDefault(pack =>
                pack.Season.Equals("winter", StringComparison.OrdinalIgnoreCase)
                && ListTracks(pack.Id).Count > 0);
            if (winter is not null)
            {
                return winter.Id;
            }
        }

        var anytime = packs.FirstOrDefault(pack =>
            pack.Season.Equals("anytime", StringComparison.OrdinalIgnoreCase)
            && ListTracks(pack.Id).Count > 0);
        return anytime?.Id;
    }

    private MusicPackDefinition? FindPack(string packId)
        => LoadCatalog().FirstOrDefault(pack => pack.Id.Equals(packId, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<MusicPackDefinition> LoadCatalog()
    {
        lock (_catalogGate)
        {
            if (_catalog is not null)
            {
                return _catalog;
            }

            var path = ResolveCatalogPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _logger.LogWarning("Music pack catalog was not found");
                _catalog = [];
                return _catalog;
            }

            var parsed = JsonSerializer.Deserialize<MusicPackCatalogFile>(File.ReadAllText(path), JsonOptions);
            _catalog = (parsed?.Packs ?? [])
                .Where(pack => !string.IsNullOrWhiteSpace(pack.Id))
                .Select(Sanitize)
                .ToList();
            return _catalog;
        }
    }

    private static MusicPackDefinition Sanitize(MusicPackDefinition pack)
    {
        pack.Id = pack.Id.Trim().ToLowerInvariant();
        pack.Name = string.IsNullOrWhiteSpace(pack.Name) ? pack.Id : pack.Name.Trim();
        pack.Season = string.IsNullOrWhiteSpace(pack.Season) ? "anytime" : pack.Season.Trim().ToLowerInvariant();
        pack.Version = MusicPackVersions.Normalize(pack.Version);
        pack.GoogleDriveFileId = string.IsNullOrWhiteSpace(pack.GoogleDriveFileId) ? null : pack.GoogleDriveFileId.Trim();
        return pack;
    }

    private static string? ResolveCatalogPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Music", "music-packs.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Music", "music-packs.json")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string MusicRoot()
    {
        var runtime = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow is not initialized.");
        Directory.CreateDirectory(runtime.MusicFolder);
        return runtime.MusicFolder;
    }

    private static string PackFolder(string packId)
        => Path.Combine(MusicRoot(), packId);

    private static IReadOnlyList<string> ListTracks(string packId)
        => ListTracksIn(PackFolder(packId));

    private static IReadOnlyList<string> ListTracksIn(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MusicPackInstallRecord? ReadInstallRecord(string packId)
    {
        var path = Path.Combine(PackFolder(packId), "pack.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MusicPackInstallRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteInstallRecord(string folder, MusicPackDefinition pack)
    {
        var record = new MusicPackInstallRecord
        {
            Id = pack.Id,
            Version = MusicPackVersions.Normalize(pack.Version)
        };
        File.WriteAllText(Path.Combine(folder, "pack.json"), JsonSerializer.Serialize(record, JsonOptions));
    }

    private static void FlattenExtractedFolder(string staging)
    {
        var entries = Directory.GetFileSystemEntries(staging)
            .Where(path => !IsJunkName(Path.GetFileName(path)))
            .ToList();
        if (entries.Count != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var inner = entries[0];
        foreach (var child in Directory.GetFileSystemEntries(inner))
        {
            var dest = Path.Combine(staging, Path.GetFileName(child));
            if (Directory.Exists(dest) || File.Exists(dest))
            {
                continue;
            }

            Directory.Move(child, dest);
        }

        Directory.Delete(inner, recursive: true);
    }

    private static void RemoveJunk(string folder)
    {
        foreach (var path in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).ToList())
        {
            if (IsJunkName(Path.GetFileName(path)))
            {
                TryDeleteDirectory(path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            var name = Path.GetFileName(path);
            if (IsJunkName(name) || string.Equals(name, "pack.json", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
            }
        }
    }

    private static bool IsJunkName(string name)
        => string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase);

    private static DateOnly TodayInScheduleZone()
    {
        var tz = ScheduleTimeZoneHelper.ResolveScheduleTimeZone();
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
    }

    private static bool IsFall(DateOnly date) => date.Month is 9 or 10 or 11;

    private static bool IsWinter(DateOnly date) => date.Month is 12 or 1 or 2;

    private static string DescribeWhen(MusicPackDefinition pack)
    {
        return pack.Season switch
        {
            "anytime" => "Plays whenever no other downloaded pack matches the current date.",
            "fall" => "Plays 1 Sep–30 Nov after you download it.",
            "winter" => "Plays 1 Dec through the end of February after you download it.",
            _ => $"Plays during {pack.Name} after you download it."
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class PackJob
    {
        public object Gate { get; } = new();

        public string Status { get; set; } = "idle";

        public string? Error { get; set; }

        public Task? Running { get; set; }
    }
}
