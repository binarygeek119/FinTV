namespace FinTv.Services;

/// <summary>
/// Active IPTV stream counts for a ChannelFlow channel.
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
