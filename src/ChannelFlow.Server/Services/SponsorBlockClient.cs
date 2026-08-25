using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FinTv.Configuration;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public sealed class SponsorBlockClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SponsorBlockClient> _logger;

    public SponsorBlockClient(IHttpClientFactory httpClientFactory, ILogger<SponsorBlockClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SponsorSkipRange>> GetSkipRangesAsync(
        string? videoId,
        YouTubeSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.SponsorBlockEnabled
            || !YouTubeUrlHelper.TryGetVideoId(videoId, out var id))
        {
            return [];
        }

        var categories = YouTubeSettings.NormalizeCategories(settings.SponsorBlockCategories);
        var query = string.Join('&', categories.Select(category => "category=" + Uri.EscapeDataString(category)));
        var url = $"https://sponsor.ajay.app/api/skipSegments?videoID={Uri.EscapeDataString(id)}&{query}";

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(SponsorBlockClient));
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("SponsorBlock returned {Status} for {VideoId}", (int)response.StatusCode, id);
                return [];
            }

            var segments = await response.Content.ReadFromJsonAsync<List<SponsorBlockSegment>>(cancellationToken)
                ?? [];
            var ranges = segments
                .Where(segment => string.Equals(segment.ActionType, "skip", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(segment.ActionType))
                .Select(ToRange)
                .Where(range => range is not null)
                .Select(range => range!)
                .ToList();

            return FfmpegSkipCuts.Merge(ranges);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "SponsorBlock lookup failed for {VideoId}", id);
            return [];
        }
    }

    private static SponsorSkipRange? ToRange(SponsorBlockSegment segment)
    {
        if (segment.Segment is not { Count: >= 2 })
        {
            return null;
        }

        var start = segment.Segment[0];
        var end = segment.Segment[1];
        if (end - start < 0.25)
        {
            return null;
        }

        return new SponsorSkipRange(start, end);
    }

    private sealed class SponsorBlockSegment
    {
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("actionType")]
        public string? ActionType { get; set; }

        [JsonPropertyName("segment")]
        public List<double>? Segment { get; set; }
    }
}

public sealed record SponsorSkipRange(double Start, double End);

public static class FfmpegSkipCuts
{
    public static IReadOnlyList<SponsorSkipRange> Merge(IEnumerable<SponsorSkipRange> ranges)
    {
        var ordered = ranges
            .Where(range => range.End - range.Start >= 0.25)
            .OrderBy(range => range.Start)
            .ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        var merged = new List<SponsorSkipRange>();
        var current = ordered[0];
        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.Start <= current.End + 0.05)
            {
                current = current with { End = Math.Max(current.End, next.End) };
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }

    /// <summary>
    /// Drops skip ranges that would remove almost the entire commercial.
    /// </summary>
    public static IReadOnlyList<SponsorSkipRange> ForPlayback(
        IReadOnlyList<SponsorSkipRange> ranges,
        double durationSeconds)
    {
        var merged = Merge(ranges);
        if (merged.Count == 0)
        {
            return merged;
        }

        var duration = Math.Max(durationSeconds, merged.Max(range => range.End));
        var skipTotal = merged.Sum(range => range.End - range.Start);
        if (duration > 0 && skipTotal / duration >= 0.8)
        {
            return [];
        }

        var kept = duration - skipTotal;
        return kept < 2 ? [] : merged;
    }

    public static string? BuildSelectExpression(IReadOnlyList<SponsorSkipRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return null;
        }

        var parts = ranges.Select(range =>
            $"between(t,{range.Start.ToString("G", CultureInfo.InvariantCulture)},{range.End.ToString("G", CultureInfo.InvariantCulture)})");
        return "not(" + string.Join('+', parts) + ")";
    }
}
