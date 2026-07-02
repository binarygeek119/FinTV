namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Active IPTV stream counts for a FinTV channel.
/// </summary>
public class ChannelStreamStatus
{
    /// <summary>
    /// Gets or sets the channel identifier.
    /// </summary>
    public Guid ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the number of active viewers on the channel stream.
    /// </summary>
    public int ViewerCount { get; set; }
}
