using System.Text;
using System.Text.Json;
using FinTv.Configuration;

namespace FinTv.Services;

public sealed class YouTubeCookieStore
{
    public const string FileName = "youtube-cookies.txt";

    private static readonly string[] SignedInNames =
    [
        "SID",
        "HSID",
        "SSID",
        "APISID",
        "SAPISID",
        "LOGIN_INFO",
        "__Secure-1PSID",
        "__Secure-3PSID",
        "__Secure-1PSIDTS",
        "__Secure-3PSIDTS"
    ];

    private readonly FinTvRuntime _runtime;
    private readonly object _gate = new();

    public YouTubeCookieStore(FinTvRuntime runtime)
    {
        _runtime = runtime;
    }

    public string FilePath => Path.Combine(_runtime.DataFolder, FileName);

    public bool HasCookies()
        => File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

    public string? GetPathIfPresent()
        => HasCookies() ? FilePath : null;

    public YouTubeCookieStatus GetStatus()
    {
        lock (_gate)
        {
            if (!HasCookies())
            {
                return new YouTubeCookieStatus(false, 0, false, null);
            }

            var lines = File.ReadAllLines(FilePath);
            var names = ParseCookieNames(lines);
            return new YouTubeCookieStatus(
                true,
                names.Count,
                names.Overlaps(SignedInNames),
                File.GetLastWriteTimeUtc(FilePath));
        }
    }

    public YouTubeCookieStatus Save(string raw)
    {
        var netscape = NormalizeToNetscape(raw);
        lock (_gate)
        {
            Directory.CreateDirectory(_runtime.DataFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, netscape, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, FilePath, overwrite: true);
            TryRestrictAccess(FilePath);
        }

        return GetStatus();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
    }

    public static string NormalizeToNetscape(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Cookie text is empty.");
        }

        var text = raw.Trim().TrimStart('\uFEFF');
        if (LooksLikeNetscape(text))
        {
            var names = ParseCookieNames(text.Split('\n'));
            if (names.Count == 0)
            {
                throw new InvalidOperationException("Netscape cookies.txt did not contain any cookie rows.");
            }

            return EnsureNetscapeHeader(text);
        }

        if (text.StartsWith('['))
        {
            return FromJsonCookies(text);
        }

        return FromHeaderCookies(text);
    }

    private static bool LooksLikeNetscape(string text)
    {
        if (text.Contains("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)
            || text.Contains("# HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Contains('\t') && text.Contains("youtube", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureNetscapeHeader(string text)
    {
        if (text.Contains("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)
            || text.Contains("# HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
        {
            return text.Replace("\r\n", "\n");
        }

        return "# Netscape HTTP Cookie File\n# https://curl.se/docs/http-cookies.html\n\n" + text.Replace("\r\n", "\n");
    }

    private static string FromHeaderCookies(string text)
    {
        var trimmed = text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            ? text["Cookie:".Length..].Trim()
            : text;

        var rows = new List<string>();
        foreach (var part in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var name = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            rows.Add(ToNetscapeRow(".youtube.com", "/", true, name, value));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("Could not parse cookies. Paste a Netscape cookies.txt export or a Cookie header string.");
        }

        return "# Netscape HTTP Cookie File\n# Converted from a Cookie header paste\n\n" + string.Join('\n', rows) + "\n";
    }

    private static string FromJsonCookies(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("JSON cookies must be an array of cookie objects.");
        }

        var rows = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var name = ReadString(item, "name") ?? ReadString(item, "Name");
            var value = ReadString(item, "value") ?? ReadString(item, "Value");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var domain = ReadString(item, "domain") ?? ReadString(item, "Domain") ?? ".youtube.com";
            var path = ReadString(item, "path") ?? ReadString(item, "Path") ?? "/";
            var secure = ReadBool(item, "secure")
                ?? ReadBool(item, "Secure")
                ?? name.StartsWith("__Secure-", StringComparison.Ordinal)
                || name.StartsWith("__Host-", StringComparison.Ordinal);
            rows.Add(ToNetscapeRow(domain, path, secure, name, value));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("JSON cookie array did not contain name/value pairs.");
        }

        return "# Netscape HTTP Cookie File\n# Converted from JSON cookie export\n\n" + string.Join('\n', rows) + "\n";
    }

    private static string ToNetscapeRow(string domain, string path, bool secure, string name, string value)
    {
        var host = domain.StartsWith('.') ? domain : "." + domain.TrimStart('.');
        if (!host.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("google", StringComparison.OrdinalIgnoreCase))
        {
            host = ".youtube.com";
        }

        var includeSubdomains = host.StartsWith('.') ? "TRUE" : "FALSE";
        var secureFlag = secure || name.StartsWith("__Secure-", StringComparison.Ordinal) || name.StartsWith("__Host-", StringComparison.Ordinal)
            ? "TRUE"
            : "FALSE";
        return $"{host}\t{includeSubdomains}\t{path}\t{secureFlag}\t0\t{name}\t{value}";
    }

    private static HashSet<string> ParseCookieNames(IEnumerable<string> lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length >= 7)
            {
                names.Add(parts[5]);
            }
        }

        return names;
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static void TryRestrictAccess(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best-effort; the file still works if chmod is unavailable.
        }
    }
}

public sealed record YouTubeCookieStatus(bool HasCookies, int CookieCount, bool LooksSignedIn, DateTime? SavedAtUtc);
