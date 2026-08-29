namespace FinTv.Domain;

/// <summary>
/// AI-picked titles that a channel may schedule from.
/// </summary>
public class ChannelCatalogPoolItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid JellyfinItemId { get; set; }

    public string Kind { get; set; } = "Series";

    public DateTime PickedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Artist names assigned to a music-video channel.
/// </summary>
public class MusicVideoChannelArtist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string ArtistName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// YouTube video or playlist URL imported for a music-video channel.
/// </summary>
public class MusicVideoYoutubeSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public string? YoutubeVideoId { get; set; }

    public string? Title { get; set; }

    public string? Artist { get; set; }

    public int? DurationSeconds { get; set; }

    public bool IsPlaylist { get; set; }

    public Guid? ParentSourceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
