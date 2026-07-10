using System.Globalization;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Builds Live TV guide metadata for weather channel programmes from a persistent AI cache.
/// </summary>
public class WeatherGuideMetadataService
{
    private static readonly SemaphoreSlim GenerateLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;
    private static int _generateWorkerActive;

    private readonly FinTvDbContext _db;
    private readonly LlmClientService _llm;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeatherGuideMetadataService> _logger;

    public WeatherGuideMetadataService(
        FinTvDbContext db,
        LlmClientService llm,
        IServiceScopeFactory scopeFactory,
        ILogger<WeatherGuideMetadataService> logger)
    {
        _db = db;
        _llm = llm;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsGenerating => Volatile.Read(ref _generateWorkerActive) > 0;

    /// <summary>
    /// Resolves guide metadata for weather playout items using the persistent cache only.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, GuideProgramMetadata>> ResolveAsync(
        IReadOnlyList<PlayoutItem> items,
        IReadOnlyDictionary<Guid, Channel> channelsById,
        Func<Channel, string?> getChannelLogoUrl,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, GuideProgramMetadata>();
        foreach (var item in items)
        {
            if (!channelsById.TryGetValue(item.ChannelId, out var channel))
            {
                continue;
            }

            result[item.Id] = ResolveOne(item, channel, getChannelLogoUrl(channel));
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, GuideProgramMetadata>>(result);
    }

    /// <summary>
    /// Queues a background job to generate missing weather guide cache entries with AI.
    /// </summary>
    public void QueueGenerateCache(bool force = false)
    {
        if (!IsAiConfigured())
        {
            throw new InvalidOperationException("AI is not enabled or API keys are not configured.");
        }

        if (IsGenerating)
        {
            return;
        }

        _logger.LogInformation("Queueing weather guide AI cache generation (force={Force})", force);
        _ = Task.Run(async () =>
        {
            await GenerateLock.WaitAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _generateWorkerActive);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var worker = scope.ServiceProvider.GetRequiredService<WeatherGuideMetadataService>();
                await worker.GenerateAllChannelsCacheAsync(force, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Weather guide AI cache generation failed");
            }
            finally
            {
                Interlocked.Decrement(ref _generateWorkerActive);
                GenerateLock.Release();
            }
        });
    }

    /// <summary>
    /// Clears all persisted weather guide AI cache entries.
    /// </summary>
    public int ClearCache()
    {
        var count = WeatherGuideCacheStore.Count();
        WeatherGuideCacheStore.Clear();
        _logger.LogInformation("Cleared {Count} weather guide AI cache entries", count);
        return count;
    }

    /// <summary>
    /// Builds cache status for the admin UI.
    /// </summary>
    public async Task<object> BuildCacheStatusAsync(CancellationToken cancellationToken = default)
    {
        var weatherChannels = await _db.Channels
            .AsNoTracking()
            .Where(c => c.Enabled && c.ContentType == ChannelContentType.Weather)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        var channels = weatherChannels.Select(channel =>
        {
            var location = NormalizeLocation(channel.WeatherLocationQuery);
            var hoursCached = Enumerable.Range(0, 24)
                .Count(hour => WeatherGuideCacheStore.Contains(BuildCacheKey(channel.Id, location, hour)));
            return new
            {
                channelId = channel.Id,
                channelName = channel.Name,
                location,
                hoursCached,
                isComplete = hoursCached >= 24
            };
        }).ToList();

        return new
        {
            isGenerating = IsGenerating,
            entryCount = WeatherGuideCacheStore.Count(),
            channelCount = weatherChannels.Count,
            completeChannels = channels.Count(c => c.isComplete),
            channels
        };
    }

    private GuideProgramMetadata ResolveOne(
        PlayoutItem item,
        Channel channel,
        string? channelLogoUrl)
    {
        var location = NormalizeLocation(channel.WeatherLocationQuery);
        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(item.Start, tz);
        var hour = localStart.Hour;
        var cacheKey = BuildCacheKey(channel.Id, location, hour);

        if (TryGetCachedMetadata(cacheKey, out var cached))
        {
            return ApplyIconUrl(cached, channelLogoUrl);
        }

        return ApplyIconUrl(BuildFallback(channel, location, localStart), channelLogoUrl);
    }

    private async Task GenerateAllChannelsCacheAsync(bool force, CancellationToken cancellationToken)
    {
        var weatherChannels = await _db.Channels
            .AsNoTracking()
            .Where(c => c.Enabled && c.ContentType == ChannelContentType.Weather)
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);

        if (weatherChannels.Count == 0)
        {
            _logger.LogInformation("Weather guide AI cache generation skipped: no enabled weather channels");
            return;
        }

        foreach (var channel in weatherChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = NormalizeLocation(channel.WeatherLocationQuery);
            var missingHours = Enumerable.Range(0, 24)
                .Where(hour => force || !IsHourCached(channel.Id, location, hour))
                .ToList();

            if (missingHours.Count == 0)
            {
                FinTvDebugLog.Ai(
                    _logger,
                    "Weather guide cache already complete for {Channel} ({Location})",
                    channel.Name,
                    location);
                continue;
            }

            FinTvDebugLog.Ai(
                _logger,
                "Generating weather guide cache for {Channel} ({Location}): {Hours} hours",
                channel.Name,
                location,
                missingHours.Count);

            var generated = await GenerateChannelHoursAsync(channel, location, missingHours, cancellationToken)
                .ConfigureAwait(false);
            SaveCacheEntries(channel.Id, location, generated);
            _logger.LogInformation(
                "Weather guide AI cache updated for {Channel}: {Count} hour slots",
                channel.Name,
                generated.Count);
        }
    }

    private async Task<Dictionary<int, WeatherGuideSlotCache>> GenerateChannelHoursAsync(
        Channel channel,
        string locationQuery,
        IReadOnlyList<int> hours,
        CancellationToken cancellationToken)
    {
        var provider = Plugin.Instance?.Configuration.Ai.DefaultProvider ?? AiProvider.OpenAi;
        var displayLocation = WeatherLocationParser.GetDisplayName(locationQuery);
        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        var hourList = string.Join(", ", hours.Select(h => h.ToString("00", CultureInfo.InvariantCulture)));

        var systemPrompt =
            "You write concise TV guide listings for a 24-hour local weather channel. " +
            "Return JSON with key hours: an array of objects with hour (0-23 integer), title, subTitle, description, categories (string array). " +
            "Each entry should suit that hour's daypart for the given location. " +
            "Do not invent live temperatures, radar, or alerts. Keep descriptions timeless and reusable daily.";

        var userPrompt =
            $"Channel: {channel.Name}\n" +
            $"Location query: {locationQuery}\n" +
            $"Display location: {displayLocation}\n" +
            $"Schedule time zone: {tz.Id}\n" +
            $"Generate guide entries for these local hours only: {hourList}\n" +
            "Use classic cable TV guide tone.";

        var json = await _llm.CompleteJsonAsync(provider, systemPrompt, userPrompt, cancellationToken);
        var parsed = JsonSerializer.Deserialize<AiWeatherGuideBatchResponse>(json, JsonOptions);
        var result = new Dictionary<int, WeatherGuideSlotCache>();
        var now = DateTime.UtcNow;

        foreach (var entry in parsed?.Hours ?? new List<AiWeatherGuideHourResponse>())
        {
            if (entry.Hour is not int hour || hour is < 0 or > 23 || !hours.Contains(hour))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Title))
            {
                continue;
            }

            var timeLabel = FormatHourLabel(hour);
            result[hour] = new WeatherGuideSlotCache
            {
                Title = entry.Title.Trim(),
                SubTitle = string.IsNullOrWhiteSpace(entry.SubTitle)
                    ? $"{displayLocation} · {timeLabel}"
                    : entry.SubTitle.Trim(),
                Description = TruncateOverview(entry.Description)
                    ?? $"Local weather forecast for {displayLocation}.",
                Categories = entry.Categories?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
                    is { Count: > 0 } categories
                    ? categories
                    : new List<string> { "Weather", "News" },
                GeneratedAtUtc = now
            };
        }

        foreach (var hour in hours.Where(h => !result.ContainsKey(h)))
        {
            result[hour] = BuildStaticCacheEntry(channel, locationQuery, hour);
        }

        return result;
    }

    private static void SaveCacheEntries(Guid channelId, string location, Dictionary<int, WeatherGuideSlotCache> entries)
    {
        WeatherGuideCacheStore.SetMany(entries.Select(pair => new KeyValuePair<string, WeatherGuideSlotCache>(
            BuildCacheKey(channelId, location, pair.Key),
            pair.Value)));
    }

    private static bool TryGetCachedMetadata(string cacheKey, out GuideProgramMetadata metadata)
    {
        metadata = new GuideProgramMetadata();
        if (!WeatherGuideCacheStore.TryGet(cacheKey, out var entry))
        {
            return false;
        }

        metadata = new GuideProgramMetadata
        {
            Title = entry.Title,
            SubTitle = entry.SubTitle,
            Description = entry.Description,
            Categories = entry.Categories,
            Language = "en"
        };
        return true;
    }

    private static bool IsHourCached(Guid channelId, string location, int hour)
        => WeatherGuideCacheStore.Contains(BuildCacheKey(channelId, location, hour));

    public static string BuildCacheKey(Guid channelId, string location, int hour)
        => $"{channelId:N}|{location}|{hour:00}";

    private static string NormalizeLocation(string? locationQuery)
        => string.IsNullOrWhiteSpace(locationQuery)
            ? WeatherStarChannelService.DefaultWeatherLocationQuery.Trim()
            : locationQuery.Trim();

    private static GuideProgramMetadata ApplyIconUrl(GuideProgramMetadata metadata, string? channelLogoUrl)
    {
        if (string.IsNullOrWhiteSpace(channelLogoUrl))
        {
            return metadata;
        }

        return new GuideProgramMetadata
        {
            Title = metadata.Title,
            SubTitle = metadata.SubTitle,
            Description = metadata.Description,
            Categories = metadata.Categories,
            IconUrl = channelLogoUrl,
            Language = metadata.Language
        };
    }

    private static GuideProgramMetadata BuildFallback(Channel channel, string locationQuery, DateTime localStart)
    {
        var displayLocation = WeatherLocationParser.GetDisplayName(locationQuery);
        var timeLabel = localStart.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return new GuideProgramMetadata
        {
            Title = "Local Weather",
            SubTitle = $"{displayLocation} · {timeLabel}",
            Description = $"Live local weather forecast for {displayLocation} on {channel.Name}.",
            Categories = new[] { "Weather" },
            Language = "en"
        };
    }

    private static WeatherGuideSlotCache BuildStaticCacheEntry(Channel channel, string locationQuery, int hour)
    {
        var displayLocation = WeatherLocationParser.GetDisplayName(locationQuery);
        var timeLabel = FormatHourLabel(hour);
        return new WeatherGuideSlotCache
        {
            Title = "Local Weather",
            SubTitle = $"{displayLocation} · {timeLabel}",
            Description = $"Local weather forecast for {displayLocation} on {channel.Name}.",
            Categories = new List<string> { "Weather" },
            GeneratedAtUtc = DateTime.UtcNow
        };
    }

    private static string FormatHourLabel(int hour)
    {
        var date = DateTime.Today.AddHours(hour);
        return date.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private static bool IsAiConfigured()
    {
        var ai = Plugin.Instance?.Configuration.Ai;
        if (ai?.Enabled != true)
        {
            return false;
        }

        return ai.DefaultProvider switch
        {
            AiProvider.Venice => !string.IsNullOrWhiteSpace(ai.VeniceApiKey),
            _ => !string.IsNullOrWhiteSpace(ai.OpenAiApiKey)
        };
    }

    private static string? TruncateOverview(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        const int maxLength = 500;
        var trimmed = overview.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..(maxLength - 3)] + "...";
    }

    private sealed class AiWeatherGuideBatchResponse
    {
        public List<AiWeatherGuideHourResponse>? Hours { get; set; }
    }

    private sealed class AiWeatherGuideHourResponse
    {
        public int? Hour { get; set; }

        public string? Title { get; set; }

        public string? SubTitle { get; set; }

        public string? Description { get; set; }

        public List<string>? Categories { get; set; }
    }
}

internal static class WeatherLocationParser
{
    public static bool TryParseLatLon(string query, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        var parts = query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)
            && Math.Abs(latitude) <= 90
            && Math.Abs(longitude) <= 180)
        {
            return true;
        }

        latitude = 0;
        longitude = 0;
        return false;
    }

    public static string GetDisplayName(string query)
    {
        if (TryParseLatLon(query, out var lat, out var lon))
        {
            return $"{lat.ToString("F2", CultureInfo.InvariantCulture)}, {lon.ToString("F2", CultureInfo.InvariantCulture)}";
        }

        var parts = query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && !LooksNumeric(parts[0]))
        {
            return $"{parts[0]}, {parts[1]}";
        }

        if (parts.Length >= 3)
        {
            return $"{parts[1]}, {parts[2]}";
        }

        if (parts.Length == 2)
        {
            return parts[1];
        }

        return query.Trim();
    }

    private static bool LooksNumeric(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}
