using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinTv;
using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed record NewsShowCopy(
    IReadOnlyList<NewsArticle> Stories,
    string? Intro,
    string? Outro);

public sealed class NewsShowWriter
{
    public const string AnchorName = "Catherine Wolfe";

    public const string ShowName = "FlowWire News";

    private readonly LlmClientService _llm;
    private readonly ILogger<NewsShowWriter> _logger;

    public NewsShowWriter(LlmClientService llm, ILogger<NewsShowWriter> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public static string ResolveShowName(string? header)
    {
        var value = header?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(value) || IsLegacyShowName(value) ? ShowName : value;
    }

    public static bool IsLegacyShowName(string value)
    {
        var compact = value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return compact.Contains("FINTV", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("CHANNELFLOW", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FlowWire", StringComparison.OrdinalIgnoreCase);
    }

    public static string DefaultIntro(string? showName = null)
        => $"I'm {AnchorName}. You're watching {ResolveShowName(showName)}.";

    public static string DefaultOutro(string? showName = null)
        => $"I'm {AnchorName}. Stay with {ResolveShowName(showName)}.";

    public async Task<NewsShowCopy> RewriteAsync(
        string header,
        IReadOnlyList<NewsArticle> articles,
        NewsSettings settings,
        CancellationToken cancellationToken)
    {
        var ai = FinTvRuntime.Current?.Configuration.Ai;
        if (ai is null)
        {
            _logger.LogWarning("News AI rewrite skipped: AI settings are not loaded.");
            return new NewsShowCopy(articles, DefaultIntro(header), DefaultOutro(header));
        }

        var hasKey = ai.DefaultProvider == AiProvider.Venice
            ? !string.IsNullOrWhiteSpace(ai.VeniceApiKey)
            : !string.IsNullOrWhiteSpace(ai.OpenAiApiKey);
        if (!hasKey)
        {
            _logger.LogWarning("News AI rewrite skipped: no API key for the default AI provider. Add it on the AI tab.");
            return new NewsShowCopy(articles, DefaultIntro(header), DefaultOutro(header));
        }

        if (articles.Count == 0)
        {
            return new NewsShowCopy(articles, DefaultIntro(header), DefaultOutro(header));
        }

        var showName = ResolveShowName(header);
        var cap = Math.Clamp(settings.ArticleCount, 1, 20);
        var bundle = new StringBuilder();
        for (var i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            bundle.Append(i + 1).Append(". ").Append(article.Title.Trim());
            if (!string.IsNullOrWhiteSpace(article.FeedName))
            {
                bundle.Append(" (").Append(article.FeedName.Trim()).Append(')');
            }

            bundle.AppendLine();
            if (!string.IsNullOrWhiteSpace(article.Summary))
            {
                bundle.AppendLine(article.Summary.Trim());
            }

            bundle.AppendLine();
        }

        var system = $$"""
            You are {{AnchorName}}, the lead news anchor for {{showName}}.
            Write a newscast that YOU present on air in first person as {{AnchorName}}.
            Combine RSS headlines into one short show.
            Drop duplicate or near-duplicate stories about the same event or subject.
            Do not invent facts, names, numbers, or quotes that are not in the source items.
            Merge overlapping items into a single story.
            The on-air show name is "{{showName}}". Always call it "{{showName}}" in intro and outro.
            Never say FinTV, FinTV News, Fin TV, ChannelFlow, or ChannelFlow News.
            intro must name yourself as {{AnchorName}} and "{{showName}}".
            outro must sign off as {{AnchorName}} from "{{showName}}" right before the outro music.
            Each story summary is what you read on camera: 2 to 4 spoken sentences, professional TV news delivery, with brief transitions. Do not repeat "I'm {{AnchorName}}" in every story.
            title is the on-screen graphic headline, not spoken if the summary already covers it.
            Reply with JSON only:
            {"intro":"spoken open","outro":"spoken sign-off","stories":[{"title":"on-screen headline","summary":"spoken copy"}]}
            """;

        var user = $"Show name: {showName}\nAnchor: {AnchorName}\nKeep at most {cap} unique stories.\nWrite in the same language as the sources.\nNever call the show FinTV News.\n\nSources:\n{bundle}";

        try
        {
            var json = await _llm.CompleteJsonAsync(ai.DefaultProvider, system, user, cancellationToken);
            var rewritten = ParseShow(json, articles, showName);
            if (rewritten.Stories.Count == 0)
            {
                _logger.LogWarning("News AI rewrite returned no stories; using original RSS items");
                return new NewsShowCopy(articles, DefaultIntro(header), DefaultOutro(header));
            }

            _logger.LogInformation(
                "News AI rewrite kept {Kept} unique stories from {Source} RSS items for {Anchor}",
                rewritten.Stories.Count,
                articles.Count,
                AnchorName);
            return rewritten;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "News AI rewrite failed; using original RSS items");
            return new NewsShowCopy(articles, DefaultIntro(header), DefaultOutro(header));
        }
    }

    private static NewsShowCopy ParseShow(string json, IReadOnlyList<NewsArticle> originals, string header)
    {
        using var doc = JsonDocument.Parse(json);
        var intro = ReadTrimmed(doc.RootElement, "intro");
        var outro = ReadTrimmed(doc.RootElement, "outro");
        if (!doc.RootElement.TryGetProperty("stories", out var stories)
            || stories.ValueKind != JsonValueKind.Array)
        {
            return new NewsShowCopy([], intro ?? DefaultIntro(header), outro ?? DefaultOutro(header));
        }

        var result = new List<NewsArticle>();
        var usedImages = new HashSet<int>();
        foreach (var row in stories.EnumerateArray())
        {
            var title = row.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            var summary = row.TryGetProperty("summary", out var summaryEl) ? summaryEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var image = MatchImage(title, originals, usedImages);
            result.Add(new NewsArticle(title.Trim(), summary?.Trim() ?? "", "News", image));
        }

        return new NewsShowCopy(
            result,
            SanitizeSpokenBrand(string.IsNullOrWhiteSpace(intro) ? DefaultIntro(header) : intro),
            SanitizeSpokenBrand(string.IsNullOrWhiteSpace(outro) ? DefaultOutro(header) : outro));
    }

    public static string SanitizeSpokenBrand(string text)
    {
        var cleaned = LegacyShowRegex.Replace(text, ShowName);
        return string.IsNullOrWhiteSpace(cleaned) ? DefaultIntro(ShowName) : cleaned;
    }

    private static readonly Regex LegacyShowRegex = new(
        @"fin\s*tv(\s+news)?|channel\s*flow(\s+news)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string? ReadTrimmed(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) ? el.GetString()?.Trim() : null;

    private static string? MatchImage(string title, IReadOnlyList<NewsArticle> originals, HashSet<int> used)
    {
        var needle = title.ToUpperInvariant();
        for (var i = 0; i < originals.Count; i++)
        {
            if (used.Contains(i) || string.IsNullOrWhiteSpace(originals[i].ImageUrl))
            {
                continue;
            }

            var hay = originals[i].Title.ToUpperInvariant();
            if (hay.Contains(needle, StringComparison.Ordinal) || needle.Contains(hay, StringComparison.Ordinal))
            {
                used.Add(i);
                return originals[i].ImageUrl;
            }
        }

        for (var i = 0; i < originals.Count; i++)
        {
            if (used.Contains(i) || string.IsNullOrWhiteSpace(originals[i].ImageUrl))
            {
                continue;
            }

            used.Add(i);
            return originals[i].ImageUrl;
        }

        return null;
    }
}
