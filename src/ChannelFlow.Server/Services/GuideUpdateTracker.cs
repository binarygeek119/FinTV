namespace FinTv.Services;

/// <summary>
/// Tracks when channel playout (XMLTV guide data) last changed so the Jellyfin plugin can refresh listings.
/// </summary>
public sealed class GuideUpdateTracker
{
    private long _revision;
    private DateTime _updatedAt = DateTime.UtcNow;

    public void MarkUpdated()
    {
        Interlocked.Increment(ref _revision);
        _updatedAt = DateTime.UtcNow;
    }

    public GuideUpdateStatus Snapshot()
        => new(Interlocked.Read(ref _revision), _updatedAt);
}

public sealed record GuideUpdateStatus(long Revision, DateTime UpdatedAt);
