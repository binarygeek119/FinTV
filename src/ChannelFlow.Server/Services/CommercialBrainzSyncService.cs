using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

public class CommercialBrainzSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CommercialBrainzClient _client;
    private readonly CommercialBrainzFilterService _filter;
    private readonly ILogger<CommercialBrainzSyncService> _logger;

    public CommercialBrainzSyncService(
        IServiceScopeFactory scopeFactory,
        CommercialBrainzClient client,
        CommercialBrainzFilterService filter,
        ILogger<CommercialBrainzSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _filter = filter;
        _logger = logger;
    }

    public async Task<CommercialBrainzPreviewResult> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        var baseUrl = CommercialBrainzSettings.NormalizeBaseUrl(settings.BaseUrl);
        var fetched = 0;
        var matched = 0;
        var samples = new List<CommercialBrainzPreviewItem>();
        const int previewCap = 48;
        const int scanCap = 120;
        var enrichDetails = PreviewNeedsEnrichment(settings);

        await foreach (var video in EnumerateCandidatesAsync(settings, enrichDetails, cancellationToken))
        {
            fetched++;
            if (!_filter.Matches(settings, video, requireEnabled: false))
            {
                if (fetched >= scanCap)
                {
                    break;
                }

                continue;
            }

            matched++;
            if (samples.Count < previewCap)
            {
                samples.Add(new CommercialBrainzPreviewItem
                {
                    Title = video.GetTitle(),
                    Brand = video.GetBrand(),
                    Year = video.GetYear(),
                    Network = video.Network,
                    ChannelName = video.ChannelName,
                    DurationSeconds = video.GetDurationSeconds(),
                    ThumbnailUrl = video.GetThumbnailUrl(baseUrl),
                    CommercialPageUrl = video.GetCommercialPageUrl(baseUrl),
                    YouTubeUrl = video.GetYouTubeUrl(),
                    YouTubeVideoId = video.GetYouTubeId()
                });
            }

            if (samples.Count >= previewCap || fetched >= scanCap || matched >= settings.MaxSyncResults)
            {
                break;
            }
        }

        return new CommercialBrainzPreviewResult
        {
            Enabled = settings.Enabled,
            FetchedCount = fetched,
            MatchedCount = matched,
            Samples = samples
        };
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        var state = settings.SyncState;
        if (state.IsRunning)
        {
            return;
        }

        state.IsRunning = true;
        state.LastError = null;
        SaveSettings(settings);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            var fetched = 0;
            var matched = 0;
            var syncedSbids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await foreach (var video in EnumerateCandidatesAsync(settings, enrichDetails: true, cancellationToken))
            {
                fetched++;
                if (!_filter.Matches(settings, video))
                {
                    continue;
                }

                matched++;
                var sbid = video.Sbid.ToString("D");
                syncedSbids.Add(sbid);

                var mapped = _filter.MapToCommercial(video);
                var existing = await db.Commercials
                    .FirstOrDefaultAsync(c => c.CommercialBrainzVideoSbid == sbid, cancellationToken);

                if (existing is null)
                {
                    db.Commercials.Add(mapped);
                }
                else
                {
                    UpdateExisting(existing, mapped);
                }

                if (matched >= settings.MaxSyncResults)
                {
                    break;
                }
            }

            var stale = await db.Commercials
                .Where(c => c.Source == CommercialSource.CommercialBrainz)
                .ToListAsync(cancellationToken);

            foreach (var commercial in stale)
            {
                if (commercial.CommercialBrainzVideoSbid is null || syncedSbids.Contains(commercial.CommercialBrainzVideoSbid))
                {
                    continue;
                }

                db.Commercials.Remove(commercial);
            }

            await db.SaveChangesAsync(cancellationToken);

            state.LastMatchedCount = matched;
            state.LastFetchedCount = fetched;
            state.LibraryCount = await db.Commercials.CountAsync(
                c => c.Source == CommercialSource.CommercialBrainz,
                cancellationToken);
            state.LastCompletedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "CommercialBrainz sync complete: matched {Matched}/{Fetched}, library {LibraryCount}",
                matched,
                fetched,
                state.LibraryCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.LastError = ex.Message;
            _logger.LogError(ex, "CommercialBrainz sync failed");
            throw;
        }
        finally
        {
            state.IsRunning = false;
            SaveSettings(settings);
        }
    }

    public Task<byte[]?> GetYouTubeThumbnailAsync(string? youtubeId, CancellationToken cancellationToken = default)
        => _client.GetYouTubeThumbnailAsync(youtubeId, cancellationToken);

    public async Task<CommercialSearchPlaylist> PullSearchPlaylistAsync(
        Guid playlistId,
        CancellationToken cancellationToken = default)
    {
        var runtime = FinTvRuntime.Current ?? throw new InvalidOperationException("ChannelFlow is not initialized.");
        var playlist = runtime.Configuration.CommercialSearchPlaylists
            .FirstOrDefault(p => p.Id == playlistId)
            ?? throw new InvalidOperationException("Search playlist not found.");

        var query = playlist.Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Search playlist query is required.");
        }

        var settings = GetSettings();
        var pullSettings = ForSearchPull(settings, playlist.MaxResults);
        var limit = Math.Clamp(playlist.MaxResults, 1, 100);
        var seen = new HashSet<Guid>();
        var videos = new List<CommercialBrainzVideoSummary>();

        try
        {
            foreach (var hit in await _client.SearchAsync(settings, query, "video", limit, cancellationToken))
            {
                if (hit.Sbid == Guid.Empty || !seen.Add(hit.Sbid))
                {
                    continue;
                }

                var detail = await _client.GetVideoAsync(settings, hit.Sbid, cancellationToken);
                if (detail is not null)
                {
                    videos.Add(detail);
                }
            }

            if (videos.Count < limit)
            {
                foreach (var hit in await _client.SearchAsync(settings, query, "advertiser", 25, cancellationToken))
                {
                    await foreach (var video in BrowseAllAsync(
                        pullSettings,
                        limit,
                        seen,
                        enrichDetails: true,
                        advertiserSbid: hit.Sbid.ToString("D"),
                        cancellationToken: cancellationToken))
                    {
                        videos.Add(video);
                        if (videos.Count >= limit)
                        {
                            break;
                        }
                    }

                    if (videos.Count >= limit)
                    {
                        break;
                    }
                }
            }

            if (videos.Count < limit)
            {
                var advertisers = await _client.SearchAdvertisersAsync(settings, query, 0, 25, cancellationToken);
                foreach (var advertiser in advertisers.Items)
                {
                    await foreach (var video in BrowseAllAsync(
                        pullSettings,
                        limit,
                        seen,
                        enrichDetails: true,
                        advertiserSbid: advertiser.Sbid.ToString("D"),
                        cancellationToken: cancellationToken))
                    {
                        videos.Add(video);
                        if (videos.Count >= limit)
                        {
                            break;
                        }
                    }

                    if (videos.Count >= limit)
                    {
                        break;
                    }
                }
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            var matchedSbids = new List<string>();

            foreach (var video in videos)
            {
                if (!_filter.Matches(pullSettings, video, requireEnabled: false))
                {
                    continue;
                }

                var mapped = _filter.MapToCommercial(video);
                if (string.IsNullOrWhiteSpace(mapped.CommercialBrainzVideoSbid))
                {
                    continue;
                }

                var existing = await db.Commercials.FirstOrDefaultAsync(
                    c => c.CommercialBrainzVideoSbid == mapped.CommercialBrainzVideoSbid,
                    cancellationToken);
                if (existing is null)
                {
                    db.Commercials.Add(mapped);
                }
                else
                {
                    UpdateExisting(existing, mapped);
                }

                matchedSbids.Add(mapped.CommercialBrainzVideoSbid);
                if (matchedSbids.Count >= limit)
                {
                    break;
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            playlist.VideoSbids = matchedSbids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            playlist.LastMatchedCount = playlist.VideoSbids.Count;
            playlist.LastSyncedAt = DateTime.UtcNow;
            playlist.LastError = null;
            runtime.SaveConfiguration();
            return playlist;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            playlist.LastError = ex.Message;
            runtime.SaveConfiguration();
            throw;
        }
    }

    public async Task<object> MapSearchPlaylistAsync(
        CommercialSearchPlaylist playlist,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var sbids = playlist.VideoSbids ?? new List<string>();
        var items = sbids.Count == 0
            ? new List<Commercial>()
            : await db.Commercials.AsNoTracking()
                .Where(c => c.CommercialBrainzVideoSbid != null && sbids.Contains(c.CommercialBrainzVideoSbid))
                .OrderBy(c => c.Title)
                .ToListAsync(cancellationToken);

        return MapSearchPlaylist(playlist, items);
    }

    public static object MapSearchPlaylist(CommercialSearchPlaylist playlist, IReadOnlyList<Commercial>? items = null)
    {
        return new
        {
            id = playlist.Id,
            name = playlist.Name,
            query = playlist.Query,
            maxResults = playlist.MaxResults,
            lastSyncedAt = playlist.LastSyncedAt,
            lastMatchedCount = playlist.LastMatchedCount,
            lastError = playlist.LastError,
            itemCount = items?.Count ?? playlist.VideoSbids.Count,
            items = (items ?? Array.Empty<Commercial>()).Select(c => new
            {
                id = c.Id,
                title = c.Title,
                brand = c.Brand,
                year = c.Year,
                durationSeconds = (int)Math.Round(c.Duration.TotalSeconds),
                youtubeUrl = c.YouTubeUrl
            })
        };
    }

    private static CommercialBrainzSettings ForSearchPull(CommercialBrainzSettings source, int maxResults)
    {
        return new CommercialBrainzSettings
        {
            Enabled = true,
            BaseUrl = source.BaseUrl,
            ApiToken = source.ApiToken,
            MaxSyncResults = Math.Clamp(maxResults, 1, 100),
            AllowSpoof = source.AllowSpoof,
            AllowFake = source.AllowFake,
            AllowReal = source.AllowReal,
            AllowAiEnhanced = source.AllowAiEnhanced,
            AllowLateNight = source.AllowLateNight,
            AllowAdultRated = source.AllowAdultRated,
            AllowBanned = source.AllowBanned
        };
    }

    private async IAsyncEnumerable<CommercialBrainzVideoSummary> EnumerateCandidatesAsync(
        CommercialBrainzSettings settings,
        bool enrichDetails,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<Guid>();
        var limit = Math.Clamp(settings.MaxSyncResults, 1, 2000);

        if (settings.Tags.Count == 1)
        {
            await foreach (var video in BrowseAllAsync(settings, limit, seen, enrichDetails, tag: settings.Tags[0], cancellationToken: cancellationToken))
            {
                yield return video;
            }

            yield break;
        }

        if (settings.Brands.Count > 0)
        {
            foreach (var brand in settings.Brands.Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                var advertisers = await _client.SearchAdvertisersAsync(settings, brand.Trim(), 0, 25, cancellationToken);
                foreach (var advertiser in advertisers.Items)
                {
                    var advertiserSbid = advertiser.Sbid.ToString("D");
                    await foreach (var video in BrowseAllAsync(settings, limit, seen, enrichDetails, advertiserSbid: advertiserSbid, cancellationToken: cancellationToken))
                    {
                        yield return video;
                    }
                }
            }

            yield break;
        }

        await foreach (var video in BrowseAllAsync(settings, limit, seen, enrichDetails, cancellationToken: cancellationToken))
        {
            yield return video;
        }
    }

    private async IAsyncEnumerable<CommercialBrainzVideoSummary> BrowseAllAsync(
        CommercialBrainzSettings settings,
        int limit,
        HashSet<Guid> seen,
        bool enrichDetails,
        string? advertiserSbid = null,
        string? tag = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Min(100, limit);
        var offset = 0;
        var maxPages = Math.Max(1, (limit / pageSize) + 2);

        for (var page = 0; page < maxPages; page++)
        {
            var response = await _client.BrowseVideosAsync(settings, offset, pageSize, advertiserSbid, tag, cancellationToken);
            if (response.Items.Count == 0)
            {
                yield break;
            }

            foreach (var item in response.Items)
            {
                if (!seen.Add(item.Sbid))
                {
                    continue;
                }

                var enriched = item;
                if (enrichDetails && NeedsDetail(settings, item))
                {
                    var detail = await _client.GetVideoAsync(settings, item.Sbid, cancellationToken);
                    if (detail is not null)
                    {
                        enriched = detail;
                    }
                }

                yield return enriched;
            }

            if (response.Items.Count < pageSize)
            {
                yield break;
            }

            offset += pageSize;
        }
    }

    private static bool PreviewNeedsEnrichment(CommercialBrainzSettings settings)
    {
        return settings.Tags.Count > 0
            || settings.ExcludeTags.Count > 0
            || settings.Brands.Count > 0
            || settings.Genres.Count > 0;
    }

    private static bool NeedsDetail(CommercialBrainzSettings settings, CommercialBrainzVideoSummary video)
    {
        if (string.IsNullOrWhiteSpace(video.GetYouTubeUrl()) && string.IsNullOrWhiteSpace(video.GetYouTubeId()))
        {
            return true;
        }

        if ((settings.Tags.Count > 0 || settings.ExcludeTags.Count > 0) && video.Tags.Count == 0)
        {
            return true;
        }

        if (settings.Brands.Count > 0 && string.IsNullOrWhiteSpace(video.GetBrand()))
        {
            return true;
        }

        if ((settings.MinYear.HasValue || settings.MaxYear.HasValue || settings.Decades.Count > 0)
            && video.GetYear() is null)
        {
            return true;
        }

        return false;
    }

    private static void UpdateExisting(Commercial existing, Commercial mapped)
    {
        existing.Title = mapped.Title;
        existing.Duration = mapped.Duration;
        existing.YouTubeUrl = mapped.YouTubeUrl;
        existing.YouTubeVideoId = mapped.YouTubeVideoId;
        existing.Brand = mapped.Brand;
        existing.Year = mapped.Year;
        existing.Decade = mapped.Decade;
        existing.Network = mapped.Network;
        existing.ChannelName = mapped.ChannelName;
        existing.AgeLimit = mapped.AgeLimit;
        existing.TagsJson = mapped.TagsJson;
        existing.IsBanned = mapped.IsBanned;
        existing.IsAdultRated = mapped.IsAdultRated;
        existing.IsLateNight = mapped.IsLateNight;
        existing.IsSpoof = mapped.IsSpoof;
        existing.IsFake = mapped.IsFake;
        existing.IsReal = mapped.IsReal;
        existing.IsAiEnhanced = mapped.IsAiEnhanced;
    }

    private static CommercialBrainzSettings GetSettings()
    {
        return FinTvRuntime.Current?.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
    }

    private static void SaveSettings(CommercialBrainzSettings settings)
    {
        if (FinTvRuntime.Current is null)
        {
            return;
        }

        FinTvRuntime.Current.Configuration.CommercialBrainz = settings;
        FinTvRuntime.Current.SaveConfiguration();
    }
}

public class CommercialBrainzPreviewResult
{
    public bool Enabled { get; set; }

    public int FetchedCount { get; set; }

    public int MatchedCount { get; set; }

    public List<CommercialBrainzPreviewItem> Samples { get; set; } = new();
}

public class CommercialBrainzPreviewItem
{
    public string Title { get; set; } = string.Empty;

    public string? Brand { get; set; }

    public int? Year { get; set; }

    public string? Network { get; set; }

    public string? ChannelName { get; set; }

    public int DurationSeconds { get; set; }

    public string? ThumbnailUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("commercialPageUrl")]
    public string? CommercialPageUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("youtubeUrl")]
    public string? YouTubeUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("youtubeVideoId")]
    public string? YouTubeVideoId { get; set; }
}
