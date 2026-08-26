using System.Text.Json;

namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Channel, slot, and special-presentation catalog filter JSON.
/// </summary>
public class FilterDefinition
{
    /// <summary>
    /// Preset identifier (e.g. channelflow-retro). Used for channel rules, not Jellyfin tag queries.
    /// Legacy fintv-* values are accepted as aliases.
    /// </summary>
    public string? PresetId { get; set; }

    public string? Genre { get; set; }

    public List<string>? Tags { get; set; }

    public string? TitleContains { get; set; }

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    public string? MinRating { get; set; }

    public string? MaxRating { get; set; }

    public static FilterDefinition? Parse(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return null;
        }

        try
        {
            return FinTvJson.Deserialize<FilterDefinition>(filterJson);
        }
        catch
        {
            return null;
        }
    }

    public const string PresetPrefix = "channelflow-";

    public const string LegacyPresetPrefix = "fintv-";

    public static bool IsChannelPresetId(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        return tag.StartsWith(PresetPrefix, StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith(LegacyPresetPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string CanonicalPresetId(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        var trimmed = tag.Trim();
        if (trimmed.StartsWith(LegacyPresetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return PresetPrefix + trimmed[LegacyPresetPrefix.Length..];
        }

        return trimmed;
    }

    public static bool PresetIdsEqual(string? left, string? right)
    {
        var a = CanonicalPresetId(left);
        var b = CanonicalPresetId(right);
        return a.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFintvChannelTag(string? tag) => IsChannelPresetId(tag);

    public static string? ExtractFintvLibraryTag(string? filterJson)
    {
        var filter = Parse(filterJson);
        if (filter is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(filter.PresetId))
        {
            return filter.PresetId;
        }

        return filter.Tags?.FirstOrDefault(IsChannelPresetId);
    }

    /// <summary>
    /// Optional Jellyfin tags from filter JSON (excludes leftover ChannelFlow/ChannelFlow preset identifiers).
    /// </summary>
    public static IReadOnlyList<string> GetOptionalJellyfinTags(string? filterJson)
    {
        var filter = Parse(filterJson);
        if (filter?.Tags is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        return filter.Tags
            .Where(tag =>
                !string.IsNullOrWhiteSpace(tag)
                && !IsChannelPresetId(tag))
            .ToList();
    }
}
