using System.Text.Json;

namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Channel, slot, and special-presentation catalog filter JSON.
/// </summary>
public class FilterDefinition
{
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
        => Parse(filterJson)?.Tags?
            .FirstOrDefault(tag => tag.StartsWith("fintv-", StringComparison.OrdinalIgnoreCase));
}
