using FinTv.Domain;

namespace FinTv.Configuration;

public class CommercialBrainzSettings
{
    public const string DefaultBaseUrl = "https://commercialbrainz.org";

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

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultBaseUrl
            : baseUrl.Trim().TrimEnd('/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "commercialbrainz.duckdns.org", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultBaseUrl;
        }

        return value;
    }
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

public class CommercialSearchPlaylist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;

    public int MaxResults { get; set; } = 50;

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

    public DateTime? LastSyncedAt { get; set; }

    public int LastMatchedCount { get; set; }

    public string? LastError { get; set; }

    public List<string> VideoSbids { get; set; } = new();

    public bool HasStructuredFilters =>
        MinYear.HasValue
        || MaxYear.HasValue
        || Decades.Count > 0
        || Brands.Count > 0
        || Tags.Count > 0
        || ExcludeTags.Count > 0
        || Genres.Count > 0
        || Networks.Count > 0
        || ChannelNames.Count > 0
        || MinAgeLimit.HasValue
        || MaxAgeLimit.HasValue;

    public CommercialBrainzSettings ToPullSettings(CommercialBrainzSettings connection)
    {
        var brands = Brands.ToList();
        if (brands.Count == 0 && !string.IsNullOrWhiteSpace(Query))
        {
            brands.Add(Query.Trim());
        }

        return new CommercialBrainzSettings
        {
            Enabled = true,
            BaseUrl = connection.BaseUrl,
            ApiToken = connection.ApiToken,
            MaxSyncResults = Math.Clamp(MaxResults, 1, 500),
            MinYear = MinYear,
            MaxYear = MaxYear,
            Decades = Decades.ToList(),
            Brands = brands,
            Tags = Tags.ToList(),
            ExcludeTags = ExcludeTags.ToList(),
            Genres = Genres.ToList(),
            Networks = Networks.ToList(),
            ChannelNames = ChannelNames.ToList(),
            MinAgeLimit = MinAgeLimit,
            MaxAgeLimit = MaxAgeLimit,
            AllowSpoof = AllowSpoof,
            AllowFake = AllowFake,
            AllowReal = AllowReal,
            AllowAiEnhanced = AllowAiEnhanced,
            AllowLateNight = AllowLateNight,
            AllowAdultRated = AllowAdultRated,
            AllowBanned = AllowBanned
        };
    }
}
