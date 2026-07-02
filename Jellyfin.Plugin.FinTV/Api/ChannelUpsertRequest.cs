using Jellyfin.Plugin.FinTV.Domain;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Channel create/update payload from the admin UI.
/// </summary>
public class ChannelUpsertRequest
{
    public decimal Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ChannelContentType ContentType { get; set; }

    public AspectRatioMode AspectRatio { get; set; }

    public bool ScanlinesEnabled { get; set; }

    public BugPlacementMode BugPlacement { get; set; }

    public Guid? LogoSetId { get; set; }

    public string? LogoFileName { get; set; }

    public string AudioLanguage { get; set; } = "eng";

    public double? WeatherLatitude { get; set; }

    public double? WeatherLongitude { get; set; }

    public Channel ToChannel()
    {
        return new Channel
        {
            Number = Number,
            Name = Name,
            Enabled = Enabled,
            ContentType = ContentType,
            AspectRatio = AspectRatio,
            ScanlinesEnabled = ScanlinesEnabled,
            BugPlacement = BugPlacement,
            LogoSetId = LogoSetId,
            LogoFileName = LogoFileName,
            AudioLanguage = AudioLanguage,
            WeatherLatitude = WeatherLatitude,
            WeatherLongitude = WeatherLongitude
        };
    }
}
