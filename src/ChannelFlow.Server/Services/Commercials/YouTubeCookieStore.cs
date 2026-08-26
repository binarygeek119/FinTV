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

    /// <summary>
    /// Host-visible Netscape cookies.txt under the ChannelFlow config folder
    /// (<c>/config/youtube-cookies.txt</c> in Docker, the Unraid appdata mount).
    /// </summary>
    public string FilePath => Path.Combine(_runtime.DataFolder, FileName);

    private YouTubeSettings Settings
        => _runtime.Configuration.YouTube ??= new YouTubeSettings();

    public bool HasCookies()
    {
        lock (_gate)
        {
            return ReadUsableText() is not null;
        }
    }

    public YouTubeCookieStatus GetStatus()
    {
        lock (_gate)
        {
            var text = ReadUsableText();
            if (text is null)
            {
                return new YouTubeCookieStatus(false, 0, false, null, FilePath);
            }

            var names = ParseCookieNames(text.Split('\n'));
            return new YouTubeCookieStatus(
                true,
                names.Count,
                names.Overlaps(SignedInNames),
                File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : Settings.CookiesSavedAtUtc,
                FilePath);
        }
    }

    public YouTubeCookieStatus Save(string raw)
    {
        var netscape = NormalizeToNetscape(raw);
        lock (_gate)
        {
            WriteHostFile(netscape);
            Settings.NetscapeCookies = netscape;
            Settings.CookiesSavedAtUtc = DateTime.UtcNow;
            _runtime.SaveConfiguration();
        }

        return GetStatus();
    }

    public string? GetPathIfPresent()
        => GetPathIfUsable();

    /// <summary>
    /// Path for yt-dlp <c>--cookies</c>. The host-mounted config file, or null when unset.
    /// </summary>
    public string? GetPathIfUsable()
    {
        lock (_gate)
        {
            var text = ReadUsableText();
            if (text is null)
            {
                return null;
            }

            if (!FileLooksUsable(FilePath))
            {
                try
                {
                    WriteHostFile(text);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return FileLooksUsable(FilePath) ? FilePath : null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Settings.NetscapeCookies = null;
            Settings.CookiesSavedAtUtc = null;
            _runtime.SaveConfiguration();
            TryDelete(FilePath);
            TryDelete(FilePath + ".tmp");
        }
    }

    /// <summary>
    /// Prefer a host-dropped cookies.txt. Fall back to the database copy and rewrite the host file.
    /// </summary>
    private string? ReadUsableText()
    {
        var fromFile = TryReadHostFile();
        if (IsUsableNetscape(fromFile))
        {
            if (!string.Equals(Settings.NetscapeCookies, fromFile, StringComparison.Ordinal))
            {
                Settings.NetscapeCookies = fromFile;
                Settings.CookiesSavedAtUtc = File.GetLastWriteTimeUtc(FilePath);
                _runtime.SaveConfiguration();
            }

            return fromFile;
        }

        var fromDb = Settings.NetscapeCookies;
        if (fromDb is null || !IsUsableNetscape(fromDb))
        {
            return null;
        }

        try
        {
            WriteHostFile(fromDb);
        }
        catch (Exception)
        {
            // Host file may be root-owned; Save() reports that to the UI.
        }

        return fromDb;
    }

    private string? TryReadHostFile()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0)
            {
                return null;
            }

            var text = File.ReadAllText(FilePath).Trim().TrimStart('\uFEFF');
            return string.IsNullOrWhiteSpace(text) ? null : EnsureNetscapeHeader(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool FileLooksUsable(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return false;
            }

            var text = File.ReadAllText(path);
            return IsUsableNetscape(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsUsableNetscape(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && LooksLikeNetscape(text)
            && ParseCookieNames(text.Split('\n')).Count > 0;

    private void WriteHostFile(string netscape)
    {
        Directory.CreateDirectory(_runtime.DataFolder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, netscape, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception) when (File.Exists(FilePath))
        {
            TryDelete(FilePath);
            if (File.Exists(FilePath))
            {
                TryDelete(temp);
                throw new InvalidOperationException(
                    $"Could not write {FilePath}. On the host, delete or chown {FileName} in the ChannelFlow appdata folder (it is likely root-owned) and save again, or drop a Netscape cookies.txt there.");
            }

            File.Move(temp, FilePath, overwrite: true);
        }

        TryAllowHostWrite(FilePath);
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
        catch (Exception)
        {
            // Best-effort; Unraid may leave a root-owned empty cookies.txt.
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
            if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
            {
                line = line["#HttpOnly_".Length..];
            }
            else if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
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

    /// <summary>
    /// 0660 so the Unraid/host user in the container group can replace the file.
    /// </summary>
    private static void TryAllowHostWrite(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        catch (Exception)
        {
            // Best-effort; the file still works if chmod is unavailable.
        }
    }
}

public sealed record YouTubeCookieStatus(
    bool HasCookies,
    int CookieCount,
    bool LooksSignedIn,
    DateTime? SavedAtUtc,
    string FilePath);
