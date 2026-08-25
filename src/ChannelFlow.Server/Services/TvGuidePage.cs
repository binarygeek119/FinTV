namespace FinTv.Services;

/// <summary>
/// JSON TV guide payload for the ChannelFlow Web UI.
/// </summary>
public sealed class TvGuidePage
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    public DateTime Now { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public IReadOnlyList<TvGuideChannel> Channels { get; set; } = Array.Empty<TvGuideChannel>();

    public IReadOnlyList<TvGuideProgram> Programs { get; set; } = Array.Empty<TvGuideProgram>();
}

/// <summary>
/// One channel row in the TV guide.
/// </summary>
public sealed class TvGuideChannel
{
    public Guid Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string LogoUrl { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;
}

/// <summary>
/// One programme block in the TV guide.
/// </summary>
public sealed class TvGuideProgram
{
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }

    public DateTime Start { get; set; }

    public DateTime Finish { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? SubTitle { get; set; }

    public string? Description { get; set; }

    public string? Episode { get; set; }

    public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();

    public int? Year { get; set; }

    public string? Rating { get; set; }

    public string? PosterUrl { get; set; }

    public bool IsNow { get; set; }

    public bool IsVirtual { get; set; }

    /// <summary>
    /// Shorts packed into this block (Looney Tunes, Rugrats, …). The grid shows only the series name.
    /// </summary>
    public IReadOnlyList<TvGuideBlockEpisode>? Episodes { get; set; }
}

/// <summary>
/// One short episode inside a combined series guide block.
/// </summary>
public sealed class TvGuideBlockEpisode
{
    public string Title { get; set; } = string.Empty;

    public string? Episode { get; set; }

    public string? Description { get; set; }
}
