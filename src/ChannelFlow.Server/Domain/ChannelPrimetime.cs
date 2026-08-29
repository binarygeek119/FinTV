namespace FinTv.Domain;

/// <summary>
/// One 30-minute primetime slot (6:00–9:00pm) with shows the AI may pick from.
/// </summary>
public class ChannelPrimetimeSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public int SlotIndex { get; set; }

    public ICollection<ChannelPrimetimeCandidate> Candidates { get; set; } = new List<ChannelPrimetimeCandidate>();
}

/// <summary>
/// A TV series assigned to a primetime slot.
/// </summary>
public class ChannelPrimetimeCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SlotId { get; set; }

    public Guid SeriesId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public ChannelPrimetimeSlot? Slot { get; set; }
}
