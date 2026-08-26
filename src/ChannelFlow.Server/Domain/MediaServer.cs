using System.Text.Json.Serialization;

namespace FinTv.Domain;

public enum MediaServerKind
{
    Jellyfin = 0,
    Emby = 1,
    Plex = 2,
    Sidecar = 3,
    Other = 4
}

public sealed class MediaServerConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MediaServerKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public string? AccessToken { get; set; }

    public string? UserId { get; set; }

    public string? SidecarRoot { get; set; }

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime? LastHealthUtc { get; set; }

    public bool? LastHealthOk { get; set; }

    public string? LastHealthMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaServerLibrary> Libraries { get; set; } = new List<MediaServerLibrary>();
}

public sealed class MediaServerLibrary
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConnectionId { get; set; }

    public string ExternalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }

    public bool SyncEnabled { get; set; } = true;

    public int ItemCount { get; set; }

    public int SortOrder { get; set; }

    [JsonIgnore]
    public MediaServerConnection? Connection { get; set; }
}

public sealed class MediaServerRemoteLibrary
{
    public string ExternalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }

    public int? ItemCount { get; set; }
}

public sealed class MediaServerHealthResult
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ServerName { get; set; }

    public string? Version { get; set; }

    public string? UserId { get; set; }

    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
