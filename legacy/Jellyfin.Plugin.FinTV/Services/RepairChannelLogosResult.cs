namespace Jellyfin.Plugin.FinTV.Services;

public class RepairChannelLogosResult
{
    public int TotalChannels { get; set; }

    public List<string> Repaired { get; set; } = new();

    public List<string> Missing { get; set; } = new();
}
