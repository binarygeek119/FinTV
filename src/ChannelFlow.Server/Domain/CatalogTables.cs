namespace FinTv.Domain;

/// <summary>
/// Shared Jellyfin media metadata stored on typed catalog tables.
/// </summary>
public abstract class CatalogMediaRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? SortName { get; set; }

    public string? Plot { get; set; }

    public string? OfficialRating { get; set; }

    public double? CommunityRating { get; set; }

    public double? CriticRating { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime? PremiereDate { get; set; }

    public long? RuntimeTicks { get; set; }

    public string? Format { get; set; }

    public string? VideoCodec { get; set; }

    public string? AudioCodec { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>
    /// Normalized picture format: <c>16:9</c>, <c>4:3</c>, or <c>other</c>.
    /// </summary>
    public string? AspectRatio { get; set; }

    public string? Path { get; set; }

    public Guid JellyfinItemId { get; set; }

    public string? ImdbId { get; set; }

    public string? TmdbId { get; set; }

    public string? TvdbId { get; set; }

    public string? MusicBrainzId { get; set; }

    public string ProviderIdsJson { get; set; } = "{}";

    public Guid? LibraryId { get; set; }

    public string? LibraryName { get; set; }

    public string? PrimaryImagePath { get; set; }

    public string GenresJson { get; set; } = "[]";

    public string StarsJson { get; set; } = "[]";

    public string StudiosJson { get; set; } = "[]";

    public string TagsJson { get; set; } = "[]";

    public string ChaptersJson { get; set; } = "[]";

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public Guid? SourceConnectionId { get; set; }

    /// <summary>
    /// True when the item was not present in the latest catalog sync for its server.
    /// </summary>
    public bool IsMissing { get; set; }

    /// <summary>
    /// UTC time the item was first marked missing. Cleared if it returns in a later sync.
    /// </summary>
    public DateTime? MissingSince { get; set; }
}

/// <summary>TV series from Jellyfin TV libraries.</summary>
public class TvShowRow : CatalogMediaRow
{
}

/// <summary>Episodes belonging to a TV series.</summary>
public class EpisodeRow : CatalogMediaRow
{
    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public Guid? SeasonId { get; set; }

    public string? SeasonName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }
}

/// <summary>Feature films from Jellyfin movie libraries.</summary>
public class MovieRow : CatalogMediaRow
{
}

/// <summary>Audio albums and tracks from Jellyfin music libraries.</summary>
public class MusicRow : CatalogMediaRow
{
    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    public string ArtistsJson { get; set; } = "[]";

    public int? TrackNumber { get; set; }

    public int? DiscNumber { get; set; }
}

/// <summary>Music videos from Jellyfin music video libraries.</summary>
public class MusicVideoRow : CatalogMediaRow
{
    public string? Album { get; set; }

    public string ArtistsJson { get; set; } = "[]";
}

/// <summary>Clips from the Past Tense News Jellyfin library.</summary>
public class PastTenseNewsRow : CatalogMediaRow
{
    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }
}
