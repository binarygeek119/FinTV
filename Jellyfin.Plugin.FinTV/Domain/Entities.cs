namespace Jellyfin.Plugin.FinTV.Domain;

public class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ChannelContentType ContentType { get; set; } = ChannelContentType.Movie;

    public AspectRatioMode AspectRatio { get; set; } = AspectRatioMode.SixteenNine;

    public bool ScanlinesEnabled { get; set; }

    public Guid? LogoSetId { get; set; }

    public string? ChannelLogoPath { get; set; }

    public string? LogoFileName { get; set; }

    public BugPlacementMode BugPlacement { get; set; } = BugPlacementMode.Auto;

    public Guid? CommercialPresetId { get; set; }

    public string AudioLanguage { get; set; } = "eng";

    public int PlayoutSeed { get; set; } = 42;

    public string PlayoutAnchorJson { get; set; } = "{}";

    public DateTime? LastPlayoutBuiltAt { get; set; }

    public double? WeatherLatitude { get; set; }

    public double? WeatherLongitude { get; set; }

    public string? FilterJson { get; set; }

    public LogoSet? LogoSet { get; set; }

    public CommercialPreset? CommercialPreset { get; set; }

    public Lineup? DefaultLineup { get; set; }

    public ICollection<LineupOverride> Overrides { get; set; } = new List<LineupOverride>();

    public ICollection<PlayoutItem> PlayoutItems { get; set; } = new List<PlayoutItem>();

    public ICollection<PlayoutHistoryEntry> History { get; set; } = new List<PlayoutHistoryEntry>();
}

public class LogoSet
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public DateTime? LastSyncedAt { get; set; }

    public ICollection<LogoSetEntry> Entries { get; set; } = new List<LogoSetEntry>();
}

public class LogoSetEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LogoSetId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public LogoSet? LogoSet { get; set; }
}

public class Lineup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string Name { get; set; } = "Default";

    public bool IsDefault { get; set; } = true;

    public Channel? Channel { get; set; }

    public ICollection<LineupSlot> Slots { get; set; } = new List<LineupSlot>();
}

public class LineupOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public LineupOverrideKind Kind { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }

    public DateOnly? SpecificDate { get; set; }

    public string Name { get; set; } = string.Empty;

    public Channel? Channel { get; set; }

    public ICollection<LineupSlot> Slots { get; set; } = new List<LineupSlot>();
}

public class LineupSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? LineupId { get; set; }

    public Guid? LineupOverrideId { get; set; }

    public int SlotIndex { get; set; }

    public Lineup? Lineup { get; set; }

    public LineupOverride? LineupOverride { get; set; }

    public ICollection<SlotCandidate> Candidates { get; set; } = new List<SlotCandidate>();
}

public class SlotCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LineupSlotId { get; set; }

    public SlotCandidateKind Kind { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public string? CollectionName { get; set; }

    public string? FilterJson { get; set; }

    public int Weight { get; set; } = 1;

    public int SortOrder { get; set; }

    public LineupSlot? LineupSlot { get; set; }
}

public class PlayoutItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public DateTime Start { get; set; }

    public DateTime Finish { get; set; }

    public TimeSpan InPoint { get; set; }

    public TimeSpan OutPoint { get; set; }

    public FillerKind FillerKind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? GuideGroup { get; set; }

    public bool IsVirtual { get; set; }

    public VirtualContentSource VirtualSource { get; set; }

    public Channel? Channel { get; set; }
}

public class PlayoutHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid? JellyfinItemId { get; set; }

    public DateTime AiredAt { get; set; }

    public string Title { get; set; } = string.Empty;

    public Channel? Channel { get; set; }
}

public class CommercialPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Default";

    public CommercialBreakMode BreakMode { get; set; } = CommercialBreakMode.ChaptersThenTimer;

    public int TimerIntervalMinutes { get; set; } = 12;

    public int PreRollCount { get; set; }

    public int PostRollCount { get; set; } = 2;

    public int MidRollCount { get; set; } = 1;

    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
}

public class Commercial
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JellyfinItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public DateTime? LastScannedAt { get; set; }

    public ICollection<CommercialChapter> Chapters { get; set; } = new List<CommercialChapter>();
}

public class CommercialChapter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommercialId { get; set; }

    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string? Name { get; set; }

    public bool DetectedByBlackframe { get; set; }

    public Commercial? Commercial { get; set; }
}
