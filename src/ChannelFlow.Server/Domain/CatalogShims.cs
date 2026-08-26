namespace FinTv.Domain;

/// <summary>
/// Synced Jellyfin library item used for catalog queries and playout (replaces Jellyfin BaseItem).
/// </summary>
public class BaseItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? SortName { get; set; }

    public string? Overview { get; set; }

    public string? Path { get; set; }

    public string? OfficialRating { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime? PremiereDate { get; set; }

    public long? RunTimeTicks { get; set; }

    public int? IndexNumber { get; set; }

    public int? ParentIndexNumber { get; set; }

    public Guid ParentId { get; set; }

    public Guid SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public Guid? LibraryId { get; set; }

    public string? LibraryName { get; set; }

    public string? CollectionType { get; set; }

    public string? PrimaryImagePath { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? VideoCodec { get; set; }

    /// <summary>
    /// Normalized picture format: <c>16:9</c>, <c>4:3</c>, or <c>other</c>.
    /// </summary>
    public string? AspectRatio { get; set; }

    public string[] Tags { get; set; } = [];

    public string[] Genres { get; set; } = [];

    public string[] Studios { get; set; } = [];

    public string[] CollectionNames { get; set; } = [];

    public List<ChapterInfo> Chapters { get; set; } = [];

    public BaseItemKind Kind { get; set; }

    public IEnumerable<BaseItem> Children { get; set; } = [];

    public bool HasImage(ImageType imageType) =>
        imageType == ImageType.Primary && !string.IsNullOrWhiteSpace(PrimaryImagePath);

    public string? GetImagePath(ImageType imageType) =>
        imageType == ImageType.Primary ? PrimaryImagePath : null;

    public BaseItemKind GetBaseItemKind() => Kind;
}

public class Episode : BaseItem
{
    public Series? Series { get; set; }

    public Episode() => Kind = BaseItemKind.Episode;
}

public class Movie : BaseItem
{
    public Movie() => Kind = BaseItemKind.Movie;
}

public class Series : BaseItem
{
    public Series() => Kind = BaseItemKind.Series;
}

public class MusicVideo : BaseItem
{
    public MusicVideo() => Kind = BaseItemKind.MusicVideo;

    public string[] Artists { get; set; } = [];
}

public class Audio : BaseItem
{
    public Audio() => Kind = BaseItemKind.Audio;
}

public class CollectionFolder : BaseItem
{
    public CollectionFolder() => Kind = BaseItemKind.Folder;
}

public class Season : BaseItem
{
    public Season() => Kind = BaseItemKind.Season;
}

public class Playlist : BaseItem
{
    public Playlist() => Kind = BaseItemKind.Playlist;
}

public enum BaseItemKind
{
    Movie = 0,
    Series = 1,
    Episode = 2,
    MusicVideo = 3,
    Audio = 4,
    Playlist = 5,
    Folder = 6,
    Video = 7,
    Season = 8
}

public enum ImageType
{
    Primary = 0
}

public class ChapterInfo
{
    public long StartPositionTicks { get; set; }

    public string? Name { get; set; }
}

public class InternalItemsQuery
{
    public bool Recursive { get; set; } = true;

    public bool IsVirtualItem { get; set; }

    public BaseItemKind[] IncludeItemTypes { get; set; } = [];

    public string[]? Tags { get; set; }

    public string[]? Genres { get; set; }

    public Guid ParentId { get; set; }

    public string? Name { get; set; }

    public string? SearchTerm { get; set; }

    public int? Limit { get; set; }

    public object? OrderBy { get; set; }
}

public class QueryResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
}

public class VirtualFolderInfo
{
    public string? ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }
}

public static class CollectionType
{
    public const string music = "music";
}

public static class CollectionTypeOptions
{
    public const string music = "music";
}

public static class ItemSortBy
{
    public const string SortName = "SortName";
    public const string ParentIndexNumber = "ParentIndexNumber";
    public const string IndexNumber = "IndexNumber";
    public const string PremiereDate = "PremiereDate";
}

public enum SortOrder
{
    Ascending = 0,
    Descending = 1
}

public interface ILibraryManager
{
    BaseItem? GetItemById(Guid id);

    QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query);

    IReadOnlyList<VirtualFolderInfo> GetVirtualFolders();

    CollectionFolder GetUserRootFolder();
}

public interface IChapterManager
{
    IReadOnlyList<ChapterInfo> GetChapters(Guid itemId);
}

public interface IScheduledTask
{
    string Name { get; }

    string Key { get; }

    string Description { get; }

    string Category { get; }

    IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

    Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken);
}

public class TaskTriggerInfo
{
    public TaskTriggerInfoType Type { get; set; }

    public long TimeOfDayTicks { get; set; }
}

public enum TaskTriggerInfoType
{
    DailyTrigger = 0
}

public interface IFfmpegLocator
{
    string EncoderPath { get; }
}

public interface IPublicBaseUrl
{
    string GetLoopbackHttpAddress();

    string GetSmartApiUrl(HttpRequest request);
}
