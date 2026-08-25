using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FinTv.Configuration;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class CommercialBrainzClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConcurrentDictionary<Guid, CommercialBrainzVideoDetail> VideoCache = new();
    private static readonly ConcurrentDictionary<string, CacheEntry> ResponseCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task<object?>> Inflight = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CommercialBrainzClient> _logger;

    public CommercialBrainzClient(IHttpClientFactory httpClientFactory, ILogger<CommercialBrainzClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CommercialBrainzBrowsePage> BrowseVideosAsync(
        CommercialBrainzSettings settings,
        int offset,
        int limit,
        string? advertiserSbid = null,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(settings.BaseUrl);
        var query = new List<string>
        {
            $"offset={offset}",
            $"limit={limit}"
        };

        if (!string.IsNullOrWhiteSpace(advertiserSbid))
        {
            query.Add($"advertiser={Uri.EscapeDataString(advertiserSbid)}");
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query.Add($"tag={Uri.EscapeDataString(tag)}");
        }

        var url = $"{baseUrl}/api/v1/browse/videos?{string.Join('&', query)}";
        return await GetAsync<CommercialBrainzBrowsePage>(settings, url, cancellationToken)
            ?? new CommercialBrainzBrowsePage();
    }

    public async Task<CommercialBrainzVideoDetail?> GetVideoAsync(
        CommercialBrainzSettings settings,
        Guid sbid,
        CancellationToken cancellationToken = default)
    {
        if (sbid != Guid.Empty && VideoCache.TryGetValue(sbid, out var cached))
        {
            return cached;
        }

        var baseUrl = NormalizeBaseUrl(settings.BaseUrl);
        var url = $"{baseUrl}/api/v1/videos/{sbid:D}";
        var detail = await GetAsync<CommercialBrainzVideoDetail>(settings, url, cancellationToken);
        if (detail is not null && sbid != Guid.Empty)
        {
            VideoCache[sbid] = detail;
        }

        return detail;
    }

    public async Task<byte[]?> GetYouTubeThumbnailAsync(string? youtubeId, CancellationToken cancellationToken = default)
    {
        if (!IsYouTubeId(youtubeId))
        {
            return null;
        }

        var id = youtubeId!.Trim();
        foreach (var url in new[]
        {
            $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
            $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
            $"https://i.ytimg.com/vi/{id}/default.jpg"
        })
        {
            var bytes = await TryGetBytesAsync(url, cancellationToken);
            if (bytes is { Length: > 32 })
            {
                return bytes;
            }
        }

        return null;
    }

    public async Task<List<CommercialBrainzSearchHit>> SearchAsync(
        CommercialBrainzSettings settings,
        string query,
        string type,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(settings.BaseUrl);
        var url = $"{baseUrl}/api/v1/search?query={Uri.EscapeDataString(query)}&type={Uri.EscapeDataString(type)}&limit={Math.Clamp(limit, 1, 100)}";
        return await GetAsync<List<CommercialBrainzSearchHit>>(settings, url, cancellationToken)
            ?? new List<CommercialBrainzSearchHit>();
    }

    public async Task<CommercialBrainzAdvertiserPage> SearchAdvertisersAsync(
        CommercialBrainzSettings settings,
        string query,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeBaseUrl(settings.BaseUrl);
        var url = $"{baseUrl}/api/v1/advertisers?q={Uri.EscapeDataString(query)}&offset={offset}&limit={limit}";
        return await GetAsync<CommercialBrainzAdvertiserPage>(settings, url, cancellationToken)
            ?? new CommercialBrainzAdvertiserPage();
    }

    public Task ThrottleAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task<T?> GetAsync<T>(CommercialBrainzSettings settings, string url, CancellationToken cancellationToken)
    {
        if (ResponseCache.TryGetValue(url, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Value is T typed ? typed : default;
        }

        while (true)
        {
            if (ResponseCache.TryGetValue(url, out cached) && cached.Expires > DateTimeOffset.UtcNow)
            {
                return cached.Value is T typedWait ? typedWait : default;
            }

            if (Inflight.TryGetValue(url, out var existing))
            {
                var shared = await existing.WaitAsync(cancellationToken);
                return shared is T fromShared ? fromShared : default;
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Inflight.TryAdd(url, tcs.Task))
            {
                continue;
            }

            try
            {
                var fetched = await FetchAsync<T>(settings, url, cancellationToken);
                if (fetched is not null)
                {
                    ResponseCache[url] = new CacheEntry(DateTimeOffset.UtcNow + CacheTtl, fetched);
                }

                tcs.TrySetResult(fetched);
                return fetched;
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                throw;
            }
            finally
            {
                Inflight.TryRemove(url, out _);
            }
        }
    }

    private async Task<T?> FetchAsync<T>(CommercialBrainzSettings settings, string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(CommercialBrainzClient));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, settings);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CommercialBrainz request failed ({Status}) for {Url}",
                    (int)response.StatusCode,
                    url);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "CommercialBrainz request failed for {Url}", url);
            return default;
        }
    }

    private readonly record struct CacheEntry(DateTimeOffset Expires, object? Value);

    private async Task<byte[]?> TryGetBytesAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var client = _httpClientFactory.CreateClient(nameof(CommercialBrainzClient));
            using var response = await client.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Thumbnail download failed for {Url}", url);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static bool IsYouTubeId(string? youtubeId)
        => !string.IsNullOrWhiteSpace(youtubeId) && YouTubeIdRegex.IsMatch(youtubeId.Trim());

    private static readonly Regex YouTubeIdRegex = new(
        @"^[A-Za-z0-9_-]{6,20}$",
        RegexOptions.Compiled);

    private static void ApplyAuth(HttpRequestMessage request, CommercialBrainzSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken.Trim());
    }

    private static string NormalizeBaseUrl(string? baseUrl)
        => CommercialBrainzSettings.NormalizeBaseUrl(baseUrl);
}

public class CommercialBrainzBrowsePage
{
    public List<CommercialBrainzVideoSummary> Items { get; set; } = new();

    public int Total { get; set; }

    public int Offset { get; set; }

    public int Limit { get; set; }
}

public class CommercialBrainzVideoSummary
{
    private static readonly System.Text.RegularExpressions.Regex YearRegex = new(
        @"\b((?:19|20)\d{2})\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public Guid Sbid { get; set; }

    public Guid CommercialId { get; set; }

    public string? YoutubeId { get; set; }

    public string? YoutubeUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    public string? CommercialTitle { get; set; }

    public string? ChannelName { get; set; }

    public int? DurationMs { get; set; }

    public string? Network { get; set; }

    public string? Visibility { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public CommercialBrainzCommercialSummary? Commercial { get; set; }

    public CommercialBrainzAdvertiserSummary? Advertiser { get; set; }

    public List<string> Tags { get; set; } = new();

    public string GetTitle()
    {
        if (!string.IsNullOrWhiteSpace(Commercial?.Title))
        {
            return Commercial.Title;
        }

        if (!string.IsNullOrWhiteSpace(CommercialTitle))
        {
            return CommercialTitle;
        }

        var youtubeTitle = GetMetadataString("youtube_title");
        if (!string.IsNullOrWhiteSpace(youtubeTitle))
        {
            return youtubeTitle;
        }

        return Advertiser?.Name ?? ChannelName ?? YoutubeId ?? "Commercial";
    }

    public string? GetBrand() => Advertiser?.Name;

    public string? GetYouTubeId()
    {
        if (!string.IsNullOrWhiteSpace(YoutubeId))
        {
            return YoutubeId.Trim();
        }

        var url = YoutubeUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in query)
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[0].Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                return uri.AbsolutePath.Trim('/');
            }
        }

        return null;
    }

    public string? GetYouTubeUrl()
    {
        if (!string.IsNullOrWhiteSpace(YoutubeUrl))
        {
            return YoutubeUrl.Trim();
        }

        var id = GetYouTubeId();
        return string.IsNullOrWhiteSpace(id) ? null : $"https://www.youtube.com/watch?v={id}";
    }

    public string? GetThumbnailUrl(string baseUrl)
    {
        var id = GetYouTubeId();
        if (!string.IsNullOrWhiteSpace(id))
        {
            return $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        }

        return ResolveMediaUrl(baseUrl, ThumbnailUrl)
            ?? ResolveMediaUrl(baseUrl, GetMetadataString("youtube_thumbnail"));
    }

    public string GetCommercialPageUrl(string baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl)
            ? CommercialBrainzSettings.DefaultBaseUrl
            : baseUrl.Trim().TrimEnd('/');

        if (CommercialId != Guid.Empty)
        {
            var page = $"{root}/commercial/{CommercialId:D}";
            return Sbid == Guid.Empty ? page : $"{page}?video={Sbid:D}";
        }

        if (Sbid != Guid.Empty)
        {
            return $"{root}/video/{Sbid:D}";
        }

        return root;
    }

    public int? GetYear()
    {
        if (Commercial?.Year is int year && year >= 1900)
        {
            return year;
        }

        foreach (var candidate in new[] { Commercial?.Title, CommercialTitle, GetMetadataString("youtube_title") })
        {
            if (TryParseYear(candidate, out var parsed))
            {
                return parsed;
            }
        }

        foreach (var tag in Tags)
        {
            if (TryParseYear(tag, out var parsed) && tag.Trim().Length == 4)
            {
                return parsed;
            }
        }

        return null;
    }

    public int GetDurationSeconds()
        => Math.Max(1, (int)Math.Round((DurationMs ?? 30000) / 1000d));

    private string? GetMetadataString(string key)
    {
        if (Metadata is null || !Metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool TryParseYear(string? text, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = YearRegex.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out year);
    }

    private static string? ResolveMediaUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return value;
        }

        if (value.StartsWith('/'))
        {
            return $"{baseUrl.TrimEnd('/')}{value}";
        }

        return $"{baseUrl.TrimEnd('/')}/{value}";
    }
}

public class CommercialBrainzVideoDetail : CommercialBrainzVideoSummary
{
    public string? Transcript { get; set; }

    public string? Slogan { get; set; }
}

public class CommercialBrainzCommercialSummary
{
    public Guid Sbid { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public int? Decade { get; set; }
}

public class CommercialBrainzAdvertiserSummary
{
    public Guid Sbid { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class CommercialBrainzAdvertiserPage
{
    public List<CommercialBrainzAdvertiserPublic> Items { get; set; } = new();

    public int Total { get; set; }

    public int Offset { get; set; }

    public int Limit { get; set; }
}

public class CommercialBrainzAdvertiserPublic
{
    public Guid Sbid { get; set; }

    public string Name { get; set; } = string.Empty;
}

public class CommercialBrainzSearchHit
{
    public string Type { get; set; } = "video";

    public Guid Sbid { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }
}
