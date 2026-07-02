namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Built-in AI lineup briefs for Binarygeek119 channel presets.
/// </summary>
public static class ChannelAiRules
{
    private static readonly Dictionary<string, ChannelAiRuleDefinition> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fintv-flashback"] = new("1970s–2000s TV and movies; exclude crime, cops, and game shows.", ChannelCatalogMode.Mixed),
        ["fintv-retro"] = new("1910s–1960s TV and movies; exclude crime, cops, and game shows.", ChannelCatalogMode.Mixed),
        ["fintv-open-swim"] = new("Kids and teen shows and movies only.", ChannelCatalogMode.Mixed),
        ["fintv-reality"] = new("Reality TV only; exclude crime, cops, and game shows.", ChannelCatalogMode.TvOnly),
        ["fintv-news"] = new("News programming only from the fintv-news library tag.", ChannelCatalogMode.TvOnly),
        ["fintv-past-tense-news"] = new("Only play content from the Past Tense News library tag.", ChannelCatalogMode.TvOnly),
        ["fintv-crime"] = new("Crime and cop themed shows only; no game shows.", ChannelCatalogMode.TvOnly),
        ["fintv-comedy"] = new("Comedy themed shows and movies.", ChannelCatalogMode.TvOnly),
        ["fintv-game-shows"] = new("Game shows only.", ChannelCatalogMode.TvOnly),
        ["fintv-education"] = new("Educational TV and documentaries (History, Discovery, science, nature).", ChannelCatalogMode.Mixed),
        ["fintv-youtube"] = new("YouTube-sourced content only from the fintv-youtube library tag.", ChannelCatalogMode.TvOnly),
        ["fintv-creature"] = new("Creature and monster movies.", ChannelCatalogMode.MovieOnly),
        ["fintv-hero"] = new("Hero-themed movies (superhero or otherwise).", ChannelCatalogMode.MovieOnly),
        ["fintv-funny"] = new("Comedian-led movies and TV.", ChannelCatalogMode.Mixed),
        ["fintv-holiday"] = new("Holiday-themed TV and movies; prefer content for the current or upcoming holiday within about one month.", ChannelCatalogMode.Mixed),
        ["fintv-parody"] = new("Parody music videos; use fine-tune keywords or artists.", ChannelCatalogMode.TvOnly),
        ["fintv-rap"] = new("Rap and hip hop music videos; use fine-tune keywords or artists.", ChannelCatalogMode.TvOnly),
        ["fintv-music-video"] = new("General music videos; use fine-tune keywords or artists.", ChannelCatalogMode.TvOnly),
    };

    /// <summary>
    /// Gets the AI rule for a library tag, if defined.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Rule definition or null.</returns>
    public static ChannelAiRuleDefinition? GetByLibraryTag(string? libraryTag)
    {
        if (string.IsNullOrWhiteSpace(libraryTag))
        {
            return null;
        }

        return Rules.TryGetValue(libraryTag, out var rule) ? rule : null;
    }

    /// <summary>
    /// Gets the AI rule brief text for a library tag.
    /// </summary>
    /// <param name="libraryTag">Preset library tag.</param>
    /// <returns>Rule brief or empty string.</returns>
    public static string GetBrief(string? libraryTag)
        => GetByLibraryTag(libraryTag)?.Brief ?? string.Empty;

    /// <summary>
    /// Resolves catalog mode from channel state and optional library tag.
    /// </summary>
    /// <param name="channel">Channel entity.</param>
    /// <param name="libraryTag">Optional library tag override.</param>
    /// <returns>Effective catalog mode.</returns>
    public static ChannelCatalogMode ResolveCatalogMode(Channel channel, string? libraryTag = null)
    {
        if (channel.CatalogMode.HasValue)
        {
            return channel.CatalogMode.Value;
        }

        var tagRule = GetByLibraryTag(libraryTag ?? ExtractLibraryTag(channel.FilterJson));
        if (tagRule is not null)
        {
            return tagRule.DefaultCatalogMode;
        }

        return channel.ContentType switch
        {
            ChannelContentType.Movie => ChannelCatalogMode.MovieOnly,
            ChannelContentType.MusicVideo => ChannelCatalogMode.TvOnly,
            _ => ChannelCatalogMode.TvOnly
        };
    }

    /// <summary>
    /// Extracts the first fintv tag from channel filter JSON.
    /// </summary>
    /// <param name="filterJson">Channel filter JSON.</param>
    /// <returns>Library tag or null.</returns>
    public static string? ExtractLibraryTag(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return null;
        }

        try
        {
            var filter = System.Text.Json.JsonSerializer.Deserialize<FilterTagsOnly>(filterJson);
            return filter?.Tags?.FirstOrDefault(t => t.StartsWith("fintv-", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private sealed class FilterTagsOnly
    {
        public List<string>? Tags { get; set; }
    }
}

/// <summary>
/// AI rule metadata for a channel preset.
/// </summary>
public class ChannelAiRuleDefinition
{
    public ChannelAiRuleDefinition(string brief, ChannelCatalogMode defaultCatalogMode)
    {
        Brief = brief;
        DefaultCatalogMode = defaultCatalogMode;
    }

    public string Brief { get; }

    public ChannelCatalogMode DefaultCatalogMode { get; }
}
