namespace Jellyfin.Plugin.FinTV.Domain;

public enum ChannelContentType
{
    TvShow = 0,
    Movie = 1,
    MusicVideo = 2,
    Music = 3,
    Weather = 4
}

public enum AspectRatioMode
{
    SixteenNine = 0,
    FourThree = 1
}

public enum BugPlacementMode
{
    Auto = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 3,
    BottomRight = 4,
    None = 5
}

public enum FillerKind
{
    None = 0,
    PreRoll = 1,
    MidRoll = 2,
    PostRoll = 3
}

public enum LineupOverrideKind
{
    DayOfWeek = 0,
    SpecificDate = 1
}

public enum SlotCandidateKind
{
    JellyfinItem = 0,
    Collection = 1,
    FilterQuery = 2
}

public enum CommercialBreakMode
{
    ChaptersThenTimer = 0,
    TimerOnly = 1,
    ChaptersOnly = 2
}

public enum VirtualContentSource
{
    None = 0,
    WeatherStar = 1,
    MusicArtSlide = 2
}

public enum EbsBackgroundMusicSource
{
    /// <summary>
    /// Pick random tracks from all Jellyfin music libraries.
    /// </summary>
    AllMusicLibraries = 0,

    /// <summary>
    /// Pick random tracks from one selected music library.
    /// </summary>
    NamedLibrary = 1
}
