namespace FinTv.Auth;

/// <summary>
/// One ChannelFlow TV app that received its own API key at pairing.
/// </summary>
public sealed class PairedTvClient
{
    public Guid Id { get; set; }

    public string ApiKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public string? AppVersion { get; set; }

    public string? OsVersion { get; set; }
}

/// <summary>
/// Admin list row. The full API key is never sent to the browser.
/// </summary>
public sealed class PairedTvClientListItem
{
    public Guid Id { get; init; }

    public string? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public string? AppVersion { get; init; }

    public string? OsVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public string KeyHint { get; init; } = "";
}

public sealed class PairedTvClientSessionRequest
{
    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public string? AppVersion { get; set; }

    public string? OsVersion { get; set; }
}

public sealed class PairedTvClientSessionResult
{
    public Guid ClientId { get; init; }

    public string ApiKey { get; init; } = "";

    public string? DeviceId { get; init; }
}

public sealed class PairedTvClientPresence
{
    public string? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public string? AppVersion { get; init; }

    public string? OsVersion { get; init; }
}
