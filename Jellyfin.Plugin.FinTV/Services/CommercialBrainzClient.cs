using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class CommercialBrainzClient
{
    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;

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
        var baseUrl = NormalizeBaseUrl(settings.BaseUrl);
        var url = $"{baseUrl}/api/v1/videos/{sbid:D}";
        return await GetAsync<CommercialBrainzVideoDetail>(settings, url, cancellationToken);
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

    private async Task<T?> GetAsync<T>(CommercialBrainzSettings settings, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, settings);

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(CommercialBrainzClient));
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

    private static void ApplyAuth(HttpRequestMessage request, CommercialBrainzSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken.Trim());
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl)
            ? CommercialBrainzSettings.DefaultBaseUrl
            : baseUrl.Trim().TrimEnd('/');
        return value;
    }
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
    public Guid Sbid { get; set; }

    public Guid CommercialId { get; set; }

    public string? YoutubeId { get; set; }

    public string? YoutubeUrl { get; set; }

    public string? ChannelName { get; set; }

    public int? DurationMs { get; set; }

    public string? Network { get; set; }

    public string? Visibility { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public CommercialBrainzCommercialSummary? Commercial { get; set; }

    public CommercialBrainzAdvertiserSummary? Advertiser { get; set; }

    public List<string> Tags { get; set; } = new();
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
