using Jellyfin.Plugin.FinTV.Domain;

namespace Jellyfin.Plugin.FinTV.Configuration;

public class CommercialBrainzSettings
{
    public const string DefaultBaseUrl = "https://commercialbrainz.duckdns.org";

    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string? ApiToken { get; set; }

    public CommercialPoolMode PoolMode { get; set; } = CommercialPoolMode.Both;

    public int MaxSyncResults { get; set; } = 500;

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    public List<int> Decades { get; set; } = new();

    public List<string> Brands { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    public List<string> ExcludeTags { get; set; } = new();

    public List<string> Genres { get; set; } = new();

    public List<string> Networks { get; set; } = new();

    public List<string> ChannelNames { get; set; } = new();

    public int? MinAgeLimit { get; set; }

    public int? MaxAgeLimit { get; set; }

    public bool AllowSpoof { get; set; } = true;

    public bool AllowFake { get; set; } = true;

    public bool AllowReal { get; set; } = true;

    public bool AllowAiEnhanced { get; set; } = true;

    public bool AllowLateNight { get; set; } = true;

    public bool AllowAdultRated { get; set; }

    public bool AllowBanned { get; set; }

    public CommercialBrainzSyncState SyncState { get; set; } = new();
}

public class CommercialBrainzSyncState
{
    public bool IsRunning { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastCompletedAt { get; set; }

    public int LastMatchedCount { get; set; }

    public int LastFetchedCount { get; set; }

    public int LibraryCount { get; set; }
}
