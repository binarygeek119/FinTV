using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Stores ChannelFlow TV client logs next to server logs and applies the same calendar retention.
/// </summary>
public sealed class ClientLogStore
{
    internal const string FolderName = "clients";
    internal const string FilePrefix = "channelflow-client-";
    internal const string MetadataFileName = "device.json";
    internal const int MaxEntriesPerRequest = 200;
    internal const int MaxMessageChars = 8_000;
    internal const int MaxExceptionChars = 16_000;
    internal const int DefaultTailBytes = 131_072;
    internal const int MaxTailBytes = 1_048_576;

    private static readonly Regex DeviceIdPattern = new(
        @"^[A-Za-z0-9._-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LogFileName = new(
        @"^channelflow-client-(?<date>\d{8})\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IWebHostEnvironment _env;
    private readonly object _gate = new();

    public ClientLogStore(IWebHostEnvironment env)
    {
        _env = env;
    }

    public string RootDirectory =>
        Path.Combine(FileLogging.ResolveDirectory(_env.ContentRootPath), FolderName);

    public ClientLogIngestResult Ingest(ClientLogIngestRequest? request)
    {
        if (request is null)
        {
            return ClientLogIngestResult.Invalid("Request body is required.");
        }

        var deviceId = SanitizeDeviceId(request.DeviceId);
        if (deviceId is null)
        {
            return ClientLogIngestResult.Invalid("deviceId is required.");
        }

        var entries = request.Entries ?? [];
        if (entries.Count == 0)
        {
            return ClientLogIngestResult.Invalid("At least one log entry is required.");
        }

        if (entries.Count > MaxEntriesPerRequest)
        {
            entries = entries.Take(MaxEntriesPerRequest).ToList();
        }

        var now = DateTimeOffset.Now;
        var lines = new StringBuilder();
        var accepted = 0;
        foreach (var entry in entries)
        {
            var message = Truncate(entry.Message, MaxMessageChars);
            if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(entry.Exception))
            {
                continue;
            }

            var timestamp = entry.Timestamp ?? now;
            lines.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            lines.Append(" [");
            lines.Append(FormatLevel(entry.Level));
            lines.Append("] ");
            var tag = Truncate(entry.Tag, 80);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                lines.Append(tag);
                lines.Append(": ");
            }

            lines.Append(message?.ReplaceLineEndings(" ") ?? string.Empty);
            lines.AppendLine();
            var exception = Truncate(entry.Exception, MaxExceptionChars);
            if (!string.IsNullOrWhiteSpace(exception))
            {
                lines.AppendLine(exception.TrimEnd());
            }

            accepted++;
        }

        if (accepted == 0)
        {
            return ClientLogIngestResult.Invalid("At least one log entry is required.");
        }

        var metadata = new ClientLogDeviceMetadata
        {
            DeviceId = deviceId,
            DeviceName = Truncate(request.DeviceName, 80),
            AppVersion = Truncate(request.AppVersion, 40),
            OsVersion = Truncate(request.OsVersion, 80),
            LastSeenAt = now
        };

        lock (_gate)
        {
            var dir = DeviceDirectory(deviceId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MetadataFileName), FinTvJson.Serialize(metadata));
            File.AppendAllText(Path.Combine(dir, ActiveFileName(now.Date)), lines.ToString());
        }

        return ClientLogIngestResult.Ok(accepted, deviceId);
    }

    public IReadOnlyList<ClientLogDeviceInfo> ListDevices()
    {
        var root = RootDirectory;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var devices = new List<ClientLogDeviceInfo>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var deviceId = Path.GetFileName(dir);
            if (!DeviceIdPattern.IsMatch(deviceId))
            {
                continue;
            }

            var metadata = ReadMetadata(dir);
            var files = ListFiles(dir);
            var latest = files.FirstOrDefault();
            devices.Add(new ClientLogDeviceInfo(
                deviceId,
                metadata?.DeviceName,
                metadata?.AppVersion,
                metadata?.OsVersion,
                metadata?.LastSeenAt ?? latest?.WrittenAt,
                latest?.Name,
                files.Sum(file => file.Bytes),
                files.Count));
        }

        return devices
            .OrderByDescending(device => device.LastSeenAt ?? DateTimeOffset.MinValue)
            .ThenBy(device => device.DeviceName ?? device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ClientLogDeviceDetail? GetDevice(string? deviceId, string? fileName, int? tailBytes)
    {
        var id = SanitizeDeviceId(deviceId);
        if (id is null)
        {
            return null;
        }

        var dir = DeviceDirectory(id);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var files = ListFiles(dir);
        var chosen = ResolveFile(files, fileName);
        var content = chosen is null ? string.Empty : ReadTail(Path.Combine(dir, chosen.Name), tailBytes);
        var metadata = ReadMetadata(dir);
        return new ClientLogDeviceDetail(
            id,
            metadata?.DeviceName,
            metadata?.AppVersion,
            metadata?.OsVersion,
            metadata?.LastSeenAt ?? chosen?.WrittenAt,
            chosen?.Name,
            content,
            files);
    }

    public int PurgeExpired(DateTime today)
    {
        var root = RootDirectory;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var oldestKeep = DateOnly.FromDateTime(today).AddDays(-FileLogging.KeptPreviousCalendarDays);
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            foreach (var path in Directory.GetFiles(dir, $"{FilePrefix}*.log"))
            {
                if (!TryParseLogDate(Path.GetFileName(path), out var fileDate) || fileDate >= oldestKeep)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"ChannelFlow: could not delete expired client log {path}: {ex.Message}");
                }
            }

            var leftover = Directory.GetFiles(dir, $"{FilePrefix}*.log");
            if (leftover.Length == 0)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"ChannelFlow: could not delete empty client log folder {dir}: {ex.Message}");
                }
            }
        }

        return removed;
    }

    internal static string? SanitizeDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value.Trim().Where(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }

    internal static bool TryParseLogDate(string fileName, out DateOnly date)
    {
        date = default;
        var match = LogFileName.Match(fileName);
        if (!match.Success)
        {
            return false;
        }

        return DateOnly.TryParseExact(
            match.Groups["date"].Value,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private string DeviceDirectory(string deviceId)
        => Path.Combine(RootDirectory, deviceId);

    private static string ActiveFileName(DateTime day)
        => $"{FilePrefix}{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{FileLogging.LogExtension}";

    private static ClientLogDeviceMetadata? ReadMetadata(string dir)
    {
        var path = Path.Combine(dir, MetadataFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return FinTvJson.Deserialize<ClientLogDeviceMetadata>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<ClientLogFileInfo> ListFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.GetFiles(dir, $"{FilePrefix}*.log")
            .Select(path => new FileInfo(path))
            .Where(info => TryParseLogDate(info.Name, out _))
            .OrderByDescending(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => new ClientLogFileInfo(
                info.Name,
                info.Length,
                new DateTimeOffset(info.LastWriteTime)))
            .ToList();
    }

    private static ClientLogFileInfo? ResolveFile(IReadOnlyList<ClientLogFileInfo> files, string? fileName)
    {
        if (files.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return files[0];
        }

        var name = Path.GetFileName(fileName.Trim());
        return files.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadTail(string path, int? tailBytes)
    {
        var maxBytes = Math.Clamp(tailBytes ?? DefaultTailBytes, 4_096, MaxTailBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        if (length == 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (start > 0)
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0 && newline + 1 < text.Length)
            {
                text = text[(newline + 1)..];
            }
        }

        return text;
    }

    private static string FormatLevel(string? level)
    {
        return (level ?? "info").Trim().ToLowerInvariant() switch
        {
            "verbose" or "trace" or "vrb" => "VRB",
            "debug" or "dbg" => "DBG",
            "warn" or "warning" or "wrn" => "WRN",
            "error" or "err" => "ERR",
            "assert" or "wtf" or "fatal" or "ftl" => "FTL",
            _ => "INF"
        };
    }

    private static string? Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }
}

public sealed class ClientLogIngestRequest
{
    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public string? AppVersion { get; set; }

    public string? OsVersion { get; set; }

    public List<ClientLogEntry>? Entries { get; set; }
}

public sealed class ClientLogEntry
{
    public DateTimeOffset? Timestamp { get; set; }

    public string? Level { get; set; }

    public string? Tag { get; set; }

    public string? Message { get; set; }

    public string? Exception { get; set; }
}

public sealed class ClientLogDeviceMetadata
{
    public string DeviceId { get; set; } = string.Empty;

    public string? DeviceName { get; set; }

    public string? AppVersion { get; set; }

    public string? OsVersion { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed record ClientLogIngestResult(bool Success, int Accepted, string? DeviceId, string? Message)
{
    public static ClientLogIngestResult Ok(int accepted, string deviceId)
        => new(true, accepted, deviceId, null);

    public static ClientLogIngestResult Invalid(string message)
        => new(false, 0, null, message);
}

public sealed record ClientLogDeviceInfo(
    string DeviceId,
    string? DeviceName,
    string? AppVersion,
    string? OsVersion,
    DateTimeOffset? LastSeenAt,
    string? LatestFile,
    long Bytes,
    int FileCount);

public sealed record ClientLogFileInfo(string Name, long Bytes, DateTimeOffset WrittenAt);

public sealed record ClientLogDeviceDetail(
    string DeviceId,
    string? DeviceName,
    string? AppVersion,
    string? OsVersion,
    DateTimeOffset? LastSeenAt,
    string? File,
    string Content,
    IReadOnlyList<ClientLogFileInfo> Files);
