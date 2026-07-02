using Jellyfin.Plugin.FinTV.Domain;

namespace Jellyfin.Plugin.FinTV.Streaming;

public sealed class EbsPlaybackPlan
{
    public EbsDisplayMode DisplayMode { get; init; } = EbsDisplayMode.SlateImage;

    public EbsAudioMode AudioMode { get; init; } = EbsAudioMode.BackgroundMusic;

    public string? SlateImagePath { get; init; }

    public string? MusicPath { get; init; }

    public double DurationSeconds { get; init; }
}
