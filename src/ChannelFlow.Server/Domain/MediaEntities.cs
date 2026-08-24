namespace FinTv.Domain;

public class MediaItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? SortName { get; set; }

    public string? Overview { get; set; }

    public BaseItemKind Kind { get; set; }

    public string? Path { get; set; }

    public Guid? ParentId { get; set; }

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime? PremiereDate { get; set; }

    public string? OfficialRating { get; set; }

    public long? RuntimeTicks { get; set; }

    public int? IndexNumber { get; set; }

    public int? ParentIndexNumber { get; set; }

    public Guid? LibraryId { get; set; }

    public string? LibraryName { get; set; }

    public string? CollectionType { get; set; }

    public string? PrimaryImagePath { get; set; }

    public string GenresJson { get; set; } = "[]";

    public string TagsJson { get; set; } = "[]";

    public string StudiosJson { get; set; } = "[]";

    public string CollectionNamesJson { get; set; } = "[]";

    public float? CommunityRating { get; set; }

    public float? CriticRating { get; set; }

    public string? Runtime { get; set; }

    public string? Album { get; set; }

    public string? MediaType { get; set; }

    public Guid? SeasonId { get; set; }

    public string? SeasonName { get; set; }

    public string? PeopleJson { get; set; }

    public string? ProviderIdsJson { get; set; }

    public string? ArtistsJson { get; set; }

    public string? AlbumArtistsJson { get; set; }

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when the item was not present in the latest Jellyfin catalog sync.
    /// </summary>
    public bool IsMissing { get; set; }

    /// <summary>
    /// UTC time the item was first marked missing. Cleared if it returns in a later sync.
    /// </summary>
    public DateTime? MissingSince { get; set; }

    public ICollection<MediaChapter> Chapters { get; set; } = new List<MediaChapter>();
}

public class MediaChapter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MediaItemId { get; set; }

    public long StartPositionTicks { get; set; }

    public string? Name { get; set; }

    public MediaItem? MediaItem { get; set; }
}

public class PathMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string JellyfinPrefix { get; set; } = string.Empty;

    public string LocalPrefix { get; set; } = string.Empty;

    public bool IgnoreCase { get; set; }

    public int SortOrder { get; set; }
}

public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppSettingsRow
{
    public int Id { get; set; } = 1;

    public string Json { get; set; } = "{}";
}

public class NewsFeed
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }
}

public class NewsSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string HeaderText { get; set; } = "FlowWire News";

    public int ArticleCount { get; set; } = 8;

    public bool TtsEnabled { get; set; } = true;

    /// <summary>
    /// <c>google</c> (translate TTS) or <c>ai</c> (OpenAI/Venice speech from the AI tab).
    /// </summary>
    public string TtsEngine { get; set; } = "google";

    /// <summary>
    /// Merge RSS items into one newscast and drop duplicate stories via the configured AI provider.
    /// </summary>
    public bool AiRewrite { get; set; }

    public string Voice { get; set; } = "en-US";

    public string? MusicLibraryId { get; set; }

    public string? MusicLibraryName { get; set; }

    public bool ShowHeader { get; set; } = true;

    public bool ReadHeadlinesOnly { get; set; }

    public string? IntroText { get; set; }

    public string? OutroText { get; set; }

    public int RefreshMinutes { get; set; } = 10;

    /// <summary>
    /// Minimum unused RSS headlines required before a 6-hour bulletin video is encoded. Always skips when there are none.
    /// </summary>
    public int MinNewStories { get; set; } = 1;

    public bool BulletinVideosEnabled { get; set; } = true;
}
