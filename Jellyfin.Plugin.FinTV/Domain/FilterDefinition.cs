using System.Text.Json;

namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Channel, slot, and special-presentation catalog filter JSON.
/// </summary>
public class FilterDefinition
{
    /// <summary>
    /// FinTV preset identifier (e.g. fintv-retro). Used for channel rules, not Jellyfin tag queries.
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

        return filter.Tags?
            .FirstOrDefault(tag => tag.StartsWith("fintv-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Optional Jellyfin tags from filter JSON (excludes FinTV preset identifiers).
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
                && !tag.StartsWith("fintv-", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
