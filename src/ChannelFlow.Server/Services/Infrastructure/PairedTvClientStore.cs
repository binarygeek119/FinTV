using System.Text.RegularExpressions;
using FinTv.Auth;
using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Persists unique ChannelFlow TV API keys under the config folder.
/// </summary>
public sealed class PairedTvClientStore
{
    internal const string FileName = "paired-clients.json";

    private static readonly Regex DeviceIdPattern = new(
        @"^[A-Za-z0-9._-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

    private readonly FinTvRuntime _runtime;
    private readonly object _gate = new();
    private List<PairedTvClient>? _cache;

    public PairedTvClientStore(FinTvRuntime runtime)
    {
        _runtime = runtime;
    }

    public string FilePath => Path.Combine(_runtime.DataFolder, FileName);

    public IReadOnlyList<PairedTvClientListItem> List()
    {
        lock (_gate)
        {
            return Load()
                .OrderByDescending(client => client.LastSeenAt ?? client.CreatedAt)
                .ThenBy(client => client.DeviceName ?? client.DeviceId ?? client.Id.ToString("N"), StringComparer.OrdinalIgnoreCase)
                .Select(ToListItem)
                .ToList();
        }
    }

    public PairedTvClient Issue()
    {
        var client = new PairedTvClient
        {
            Id = Guid.NewGuid(),
            ApiKey = PluginApiKey.Generate(),
            CreatedAt = DateTimeOffset.Now
        };
        lock (_gate)
        {
            var clients = Load();
            clients.Add(client);
            Save(clients);
        }

        return client;
    }

    public PairedTvClient? FindByApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        lock (_gate)
        {
            return Load().FirstOrDefault(client => PluginApiKey.KeysEqual(client.ApiKey, apiKey));
        }
    }

    public bool TryAuthenticate(string? provided, out PairedTvClient? client)
    {
        client = FindByApiKey(provided);
        return client is not null;
    }

    public PairedTvClientSessionResult OpenSession(string providedKey, PairedTvClientSessionRequest? request)
    {
        var presence = Sanitize(request);
        lock (_gate)
        {
            var clients = Load();
            var existing = clients.FirstOrDefault(client => PluginApiKey.KeysEqual(client.ApiKey, providedKey));
            if (existing is not null)
            {
                ApplyPresence(existing, presence, force: true);
                Save(clients);
                return ToSession(existing);
            }

            if (!PluginApiKey.Matches(providedKey))
            {
                throw new InvalidOperationException("Invalid API key.");
            }

            PairedTvClient client;
            if (!string.IsNullOrWhiteSpace(presence.DeviceId))
            {
                client = clients.FirstOrDefault(row =>
                    string.Equals(row.DeviceId, presence.DeviceId, StringComparison.OrdinalIgnoreCase))
                    ?? IssueUnlocked(clients);
            }
            else
            {
                client = IssueUnlocked(clients);
            }

            ApplyPresence(client, presence, force: true);
            Save(clients);
            return ToSession(client);
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var clients = Load();
            var removed = clients.RemoveAll(client => client.Id == id);
            if (removed == 0)
            {
                return false;
            }

            Save(clients);
            return true;
        }
    }

    public bool RemoveByApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        lock (_gate)
        {
            var clients = Load();
            var removed = clients.RemoveAll(client => PluginApiKey.KeysEqual(client.ApiKey, apiKey));
            if (removed == 0)
            {
                return false;
            }

            Save(clients);
            return true;
        }
    }

    public void Touch(string? apiKey, PairedTvClientPresence? presence = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        lock (_gate)
        {
            var clients = Load();
            var client = clients.FirstOrDefault(row => PluginApiKey.KeysEqual(row.ApiKey, apiKey));
            if (client is null)
            {
                return;
            }

            ApplyPresence(client, presence ?? new PairedTvClientPresence(), force: false);
            Save(clients);
        }
    }

    private PairedTvClient IssueUnlocked(List<PairedTvClient> clients)
    {
        var client = new PairedTvClient
        {
            Id = Guid.NewGuid(),
            ApiKey = PluginApiKey.Generate(),
            CreatedAt = DateTimeOffset.Now
        };
        clients.Add(client);
        return client;
    }

    private static void ApplyPresence(PairedTvClient client, PairedTvClientPresence presence, bool force)
    {
        var now = DateTimeOffset.Now;
        var stale = client.LastSeenAt is null || now - client.LastSeenAt.Value >= TouchInterval;
        var changed = false;
        if (!string.IsNullOrWhiteSpace(presence.DeviceId)
            && !string.Equals(client.DeviceId, presence.DeviceId, StringComparison.Ordinal))
        {
            client.DeviceId = presence.DeviceId;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(presence.DeviceName)
            && !string.Equals(client.DeviceName, presence.DeviceName, StringComparison.Ordinal))
        {
            client.DeviceName = presence.DeviceName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(presence.AppVersion)
            && !string.Equals(client.AppVersion, presence.AppVersion, StringComparison.Ordinal))
        {
            client.AppVersion = presence.AppVersion;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(presence.OsVersion)
            && !string.Equals(client.OsVersion, presence.OsVersion, StringComparison.Ordinal))
        {
            client.OsVersion = presence.OsVersion;
            changed = true;
        }

        if (force || stale || changed)
        {
            client.LastSeenAt = now;
        }
    }

    private List<PairedTvClient> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _cache = FinTvJson.Deserialize<List<PairedTvClient>>(json) ?? [];
            }
            else
            {
                _cache = [];
            }
        }
        catch (Exception)
        {
            _cache = [];
        }

        return _cache;
    }

    private void Save(List<PairedTvClient> clients)
    {
        _cache = clients;
        Directory.CreateDirectory(_runtime.DataFolder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, FinTvJson.Serialize(clients));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static PairedTvClientListItem ToListItem(PairedTvClient client)
        => new()
        {
            Id = client.Id,
            DeviceId = client.DeviceId,
            DeviceName = client.DeviceName,
            AppVersion = client.AppVersion,
            OsVersion = client.OsVersion,
            CreatedAt = client.CreatedAt,
            LastSeenAt = client.LastSeenAt,
            KeyHint = KeyHint(client.ApiKey)
        };

    private static PairedTvClientSessionResult ToSession(PairedTvClient client)
        => new()
        {
            ClientId = client.Id,
            ApiKey = client.ApiKey,
            DeviceId = client.DeviceId
        };

    private static string KeyHint(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 4)
        {
            return "••••";
        }

        return "••••" + apiKey[^4..];
    }

    private static PairedTvClientPresence Sanitize(PairedTvClientSessionRequest? request)
    {
        var deviceId = request?.DeviceId?.Trim() ?? "";
        if (!DeviceIdPattern.IsMatch(deviceId))
        {
            deviceId = "";
        }

        return new PairedTvClientPresence
        {
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId,
            DeviceName = Truncate(request?.DeviceName, 80),
            AppVersion = Truncate(request?.AppVersion, 40),
            OsVersion = Truncate(request?.OsVersion, 80)
        };
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
