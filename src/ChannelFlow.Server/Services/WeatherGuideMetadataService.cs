using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Builds Live TV guide metadata for weather channel programmes from a persistent AI cache.
/// </summary>
public class WeatherGuideMetadataService
{
    private const int HoursPerAiBatch = 4;

    private static readonly SemaphoreSlim GenerateLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;
    private static int _generateWorkerActive;

    private readonly FinTvDbContext _db;
    private readonly LlmClientService _llm;
    private readonly WeatherDataClient _weather;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeatherGuideMetadataService> _logger;

    public WeatherGuideMetadataService(
        FinTvDbContext db,
        LlmClientService llm,
        WeatherDataClient weather,
        IServiceScopeFactory scopeFactory,
        ILogger<WeatherGuideMetadataService> logger)
    {
        _db = db;
        _llm = llm;
        _weather = weather;
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
    /// Queues a background job to generate today's weather guide from the Weather tab source.
    /// AI writes TV-guide copy when configured; otherwise live forecast facts are used directly.
    /// </summary>
    public void QueueGenerateCache(bool force = false)
    {
        if (IsGenerating)
        {
            return;
        }

        _logger.LogInformation("Queueing weather guide cache generation (force={Force}, ai={Ai})", force, IsAiConfigured());
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
                _logger.LogWarning(ex, "Weather guide cache generation failed");
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

        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        var forecastDate = LocalForecastDate(tz);
        var channels = weatherChannels.Select(channel =>
        {
            var location = NormalizeLocation(channel.WeatherLocationQuery);
            var hoursCached = Enumerable.Range(0, 24)
                .Count(hour => WeatherGuideCacheStore.Contains(BuildCacheKey(channel.Id, location, forecastDate, hour)));
            return new
            {
                channelId = channel.Id,
                channelName = channel.Name,
                location,
                hoursCached,
                isComplete = hoursCached >= 24
            };
        }).ToList();

        var lastGenerated = weatherChannels
            .SelectMany(channel => Enumerable.Range(0, 24)
                .Select(hour => BuildCacheKey(channel.Id, NormalizeLocation(channel.WeatherLocationQuery), forecastDate, hour)))
            .Select(key => WeatherGuideCacheStore.TryGet(key, out var slot) ? slot : null)
            .Where(slot => slot is not null)
            .Select(slot => slot!.GeneratedAtUtc)
            .DefaultIfEmpty()
            .Max();

        return new
        {
            isGenerating = IsGenerating,
            entryCount = WeatherGuideCacheStore.Count(),
            channelCount = weatherChannels.Count,
            completeChannels = channels.Count(c => c.isComplete),
            forecastDate = forecastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lastGeneratedAt = lastGenerated == default ? (DateTime?)null : lastGenerated,
            weatherSource = string.IsNullOrWhiteSpace(FinTvRuntime.Current?.Configuration.WeatherSource)
                ? "auto"
                : FinTvRuntime.Current!.Configuration.WeatherSource,
            aiEnabled = IsAiConfigured(),
            midnightRefresh = true,
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
        var date = DateOnly.FromDateTime(localStart);
        var cacheKey = BuildCacheKey(channel.Id, location, date, hour);

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
            _logger.LogInformation("Weather guide cache generation skipped: no enabled weather channels");
            return;
        }

        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        var forecastDate = LocalForecastDate(tz);
        PruneStaleCache(forecastDate);

        foreach (var channel in weatherChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = NormalizeLocation(channel.WeatherLocationQuery);
            var missingHours = Enumerable.Range(0, 24)
                .Where(hour => force || !IsHourCached(channel.Id, location, forecastDate, hour))
                .ToList();

            if (missingHours.Count == 0)
            {
                FinTvDebugLog.Ai(
                    _logger,
                    "Weather guide cache already complete for {Channel} ({Location}) on {Date}",
                    channel.Name,
                    location,
                    forecastDate);
                continue;
            }

            WeatherSnapshot? snapshot = null;
            try
            {
                snapshot = await FetchChannelSnapshotAsync(channel, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Weather tab source failed for {Channel} ({Location}); writing static guide copy",
                    channel.Name,
                    location);
            }

            FinTvDebugLog.Ai(
                _logger,
                "Generating weather guide cache for {Channel} ({Location}) on {Date}: {Hours} hours from {Backend}",
                channel.Name,
                location,
                forecastDate,
                missingHours.Count,
                snapshot?.Backend ?? "none");

            var generatedCount = 0;
            foreach (var hourBatch in missingHours.Chunk(HoursPerAiBatch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchHours = hourBatch.ToList();
                Dictionary<int, WeatherGuideSlotCache> generated;
                try
                {
                    generated = await GenerateChannelHoursAsync(
                            channel,
                            location,
                            forecastDate,
                            batchHours,
                            snapshot,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransientLlmFailure(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Weather guide AI batch failed for {Channel} hours {Hours}; using live forecast facts",
                        channel.Name,
                        string.Join(", ", batchHours.Select(h => h.ToString("00", CultureInfo.InvariantCulture))));
                    generated = batchHours.ToDictionary(
                        hour => hour,
                        hour => BuildLiveCacheEntry(channel, location, forecastDate, hour, snapshot));
                }

                SaveCacheEntries(channel.Id, location, forecastDate, generated);
                generatedCount += generated.Count;
            }

            _logger.LogInformation(
                "Weather guide cache updated for {Channel} on {Date}: {Count} hour slots",
                channel.Name,
                forecastDate,
                generatedCount);
        }
    }

    private static bool IsTransientLlmFailure(Exception ex)
    {
        if (ex is TaskCanceledException or HttpRequestException)
        {
            return true;
        }

        if (ex is InvalidOperationException invalid
            && invalid.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.InnerException is not null && IsTransientLlmFailure(ex.InnerException);
    }

    private async Task<Dictionary<int, WeatherGuideSlotCache>> GenerateChannelHoursAsync(
        Channel channel,
        string locationQuery,
        DateOnly forecastDate,
        IReadOnlyList<int> hours,
        WeatherSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (!IsAiConfigured() || snapshot is null)
        {
            return hours.ToDictionary(
                hour => hour,
                hour => BuildLiveCacheEntry(channel, locationQuery, forecastDate, hour, snapshot));
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await GenerateChannelHoursCoreAsync(
                        channel,
                        locationQuery,
                        forecastDate,
                        hours,
                        snapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 2 && IsTransientLlmFailure(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Weather guide AI batch attempt {Attempt} failed for {Channel}; retrying",
                    attempt,
                    channel.Name);
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Weather guide AI batch failed after retries.");
    }

    private async Task<Dictionary<int, WeatherGuideSlotCache>> GenerateChannelHoursCoreAsync(
        Channel channel,
        string locationQuery,
        DateOnly forecastDate,
        IReadOnlyList<int> hours,
        WeatherSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var provider = FinTvRuntime.Current?.Configuration.Ai.DefaultProvider ?? AiProvider.OpenAi;
        var displayLocation = snapshot.Place.DisplayName;
        if (string.IsNullOrWhiteSpace(displayLocation))
        {
            displayLocation = WeatherLocationParser.GetDisplayName(locationQuery);
        }

        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        var hourList = string.Join(", ", hours.Select(h => h.ToString("00", CultureInfo.InvariantCulture)));
        var facts = BuildForecastFactSheet(snapshot, tz, forecastDate, hours);

        var systemPrompt =
            "You write concise TV guide listings for a 24-hour local weather channel. " +
            "Return JSON with key hours: an array of objects with hour (0-23 integer), title, subTitle, description, categories (string array). " +
            "Use only the live forecast facts provided. Include the real temperature and conditions in each title. " +
            "Do not invent temperatures, radar, or alerts. Write for this calendar day only.";

        var userPrompt =
            $"Channel: {channel.Name}\n" +
            $"Location query: {locationQuery}\n" +
            $"Display location: {displayLocation}\n" +
            $"Schedule time zone: {tz.Id}\n" +
            $"Forecast date: {forecastDate:yyyy-MM-dd}\n" +
            $"Weather source: {snapshot.Backend}\n" +
            $"Generate guide entries for these local hours only: {hourList}\n" +
            "Use classic cable TV guide tone.\n\n" +
            facts;

        var json = await _llm.CompleteJsonAsync(provider, systemPrompt, userPrompt, cancellationToken);
        var parsed = JsonSerializer.Deserialize<AiWeatherGuideBatchResponse>(json, JsonOptions);
        var result = new Dictionary<int, WeatherGuideSlotCache>();
        var now = DateTime.UtcNow;
        var dateLabel = forecastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
                    ?? BuildLiveDescription(channel, displayLocation, forecastDate, hour, snapshot),
                Categories = entry.Categories?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
                    is { Count: > 0 } categories
                    ? categories
                    : DefaultCategories(snapshot),
                ForecastDate = dateLabel,
                GeneratedAtUtc = now
            };
        }

        foreach (var hour in hours.Where(h => !result.ContainsKey(h)))
        {
            result[hour] = BuildLiveCacheEntry(channel, locationQuery, forecastDate, hour, snapshot);
        }

        return result;
    }

    private async Task<WeatherSnapshot> FetchChannelSnapshotAsync(Channel channel, CancellationToken cancellationToken)
    {
        var config = FinTvRuntime.Current?.Configuration;
        var locationQuery = string.IsNullOrWhiteSpace(channel.WeatherLocationQuery)
            ? WeatherStarChannelService.ResolveDefaultLocationQuery()
            : channel.WeatherLocationQuery.Trim();
        var source = WeatherDataClient.ParseSource(config?.WeatherSource);
        var useMetric = WeatherStarChannelService.PermalinkUsesMetricUnits(config?.WeatherStarPermalinkQuery);
        return await _weather.GetSnapshotAsync(locationQuery, source, useMetric, cancellationToken);
    }

    private static void SaveCacheEntries(
        Guid channelId,
        string location,
        DateOnly forecastDate,
        Dictionary<int, WeatherGuideSlotCache> entries)
    {
        WeatherGuideCacheStore.SetMany(entries.Select(pair => new KeyValuePair<string, WeatherGuideSlotCache>(
            BuildCacheKey(channelId, location, forecastDate, pair.Key),
            pair.Value)));
        PruneStaleCache(forecastDate);
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

    private static bool IsHourCached(Guid channelId, string location, DateOnly date, int hour)
        => WeatherGuideCacheStore.Contains(BuildCacheKey(channelId, location, date, hour));

    public static string BuildCacheKey(Guid channelId, string location, DateOnly date, int hour)
        => $"{channelId:N}|{location}|{date:yyyy-MM-dd}|{hour:00}";

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

    private static WeatherGuideSlotCache BuildLiveCacheEntry(
        Channel channel,
        string locationQuery,
        DateOnly forecastDate,
        int hour,
        WeatherSnapshot? snapshot)
    {
        var displayLocation = snapshot?.Place.DisplayName;
        if (string.IsNullOrWhiteSpace(displayLocation))
        {
            displayLocation = WeatherLocationParser.GetDisplayName(locationQuery);
        }

        var timeLabel = FormatHourLabel(hour);
        var hourRow = MatchHour(snapshot, forecastDate, hour);
        var condition = HourCondition(hourRow, snapshot);
        var temp = FormatTemperature(hourRow?.Temperature ?? snapshot?.Current?.Temperature, snapshot?.UseMetric == true);
        var title = string.IsNullOrWhiteSpace(temp)
            ? condition
            : $"{condition} · {temp}";
        return new WeatherGuideSlotCache
        {
            Title = title,
            SubTitle = $"{displayLocation} · {timeLabel}",
            Description = BuildLiveDescription(channel, displayLocation, forecastDate, hour, snapshot),
            Categories = DefaultCategories(snapshot),
            ForecastDate = forecastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            GeneratedAtUtc = DateTime.UtcNow
        };
    }

    private static string BuildLiveDescription(
        Channel channel,
        string displayLocation,
        DateOnly forecastDate,
        int hour,
        WeatherSnapshot? snapshot)
    {
        var sb = new StringBuilder();
        sb.Append("Live local weather for ")
            .Append(displayLocation)
            .Append(" on ")
            .Append(forecastDate.ToString("dddd, MMMM d", CultureInfo.InvariantCulture))
            .Append(" at ")
            .Append(FormatHourLabel(hour))
            .Append(" (")
            .Append(channel.Name)
            .Append(").");

        var hourRow = MatchHour(snapshot, forecastDate, hour);
        if (hourRow is not null)
        {
            sb.Append(' ').Append(HourCondition(hourRow, snapshot)).Append('.');
            var temp = FormatTemperature(hourRow.Temperature, snapshot?.UseMetric == true);
            if (!string.IsNullOrWhiteSpace(temp))
            {
                sb.Append(" Temperature ").Append(temp).Append('.');
            }

            if (hourRow.PrecipitationChance is int pop)
            {
                sb.Append(" Chance of precipitation ").Append(pop).Append('%').Append('.');
            }

            if (hourRow.WindSpeed is double wind)
            {
                sb.Append(" Wind ");
                if (!string.IsNullOrWhiteSpace(hourRow.WindDirection))
                {
                    sb.Append(hourRow.WindDirection).Append(' ');
                }

                sb.Append(Math.Round(wind).ToString("0", CultureInfo.InvariantCulture))
                    .Append(snapshot?.UseMetric == true ? " km/h." : " mph.");
            }
        }

        var today = snapshot?.Daily.FirstOrDefault(day => day.Date == forecastDate)
            ?? snapshot?.Daily.FirstOrDefault();
        if (today is not null)
        {
            if (!string.IsNullOrWhiteSpace(today.Narrative))
            {
                sb.Append(' ').Append(today.Narrative.Trim().TrimEnd('.')).Append('.');
            }

            var high = FormatTemperature(today.High, snapshot?.UseMetric == true);
            var low = FormatTemperature(today.Low, snapshot?.UseMetric == true);
            if (!string.IsNullOrWhiteSpace(high) || !string.IsNullOrWhiteSpace(low))
            {
                sb.Append(" High ").Append(high ?? "n/a").Append(", low ").Append(low ?? "n/a").Append('.');
            }
        }

        if (snapshot?.Alerts is { Count: > 0 } alerts)
        {
            sb.Append(" Alerts: ")
                .Append(string.Join("; ", alerts.Select(alert =>
                    string.IsNullOrWhiteSpace(alert.Headline) ? alert.Event : alert.Headline).Take(3)))
                .Append('.');
        }

        return TruncateOverview(sb.ToString()) ?? sb.ToString();
    }

    private static string BuildForecastFactSheet(
        WeatherSnapshot snapshot,
        TimeZoneInfo tz,
        DateOnly forecastDate,
        IReadOnlyList<int> hours)
    {
        var unit = snapshot.UseMetric ? "C" : "F";
        var windUnit = snapshot.UseMetric ? "km/h" : "mph";
        var sb = new StringBuilder();
        sb.AppendLine($"Fetched: {snapshot.FetchedAt:u}");
        sb.AppendLine($"Place: {snapshot.Place.DisplayName}");
        sb.AppendLine($"Backend: {snapshot.Backend}");
        sb.AppendLine($"Units: °{unit}, {windUnit}");
        if (snapshot.Current is { } current)
        {
            sb.AppendLine(
                $"Current: {current.ConditionText}, {FormatTemperature(current.Temperature, snapshot.UseMetric)}");
        }

        var today = snapshot.Daily.FirstOrDefault(day => day.Date == forecastDate) ?? snapshot.Daily.FirstOrDefault();
        if (today is not null)
        {
            sb.AppendLine(
                $"Daily: {today.Narrative}. High {FormatTemperature(today.High, snapshot.UseMetric)}, low {FormatTemperature(today.Low, snapshot.UseMetric)}.");
        }

        foreach (var period in snapshot.Periods.Take(4))
        {
            sb.AppendLine($"Period {period.Name}: {period.Narrative}");
        }

        if (snapshot.Alerts.Count > 0)
        {
            sb.AppendLine("Alerts:");
            foreach (var alert in snapshot.Alerts.Take(5))
            {
                sb.AppendLine($"- {alert.Event}: {(string.IsNullOrWhiteSpace(alert.Headline) ? alert.Description : alert.Headline)}");
            }
        }
        else
        {
            sb.AppendLine("Alerts: none");
        }

        sb.AppendLine("Hourly facts:");
        foreach (var hour in hours)
        {
            var row = MatchHour(snapshot, forecastDate, hour);
            if (row is null)
            {
                sb.AppendLine($"{hour:00}:00 local — no hour-specific forecast; use today's daily summary.");
                continue;
            }

            var local = TimeZoneInfo.ConvertTime(row.Time, tz);
            sb.Append(hour.ToString("00", CultureInfo.InvariantCulture))
                .Append(":00 local (")
                .Append(local.ToString("HH:mm", CultureInfo.InvariantCulture))
                .Append(") ")
                .Append(HourCondition(row, snapshot))
                .Append(", ")
                .Append(FormatTemperature(row.Temperature, snapshot.UseMetric));
            if (row.PrecipitationChance is int pop)
            {
                sb.Append(", precip ").Append(pop).Append('%');
            }

            if (row.WindSpeed is double wind)
            {
                sb.Append(", wind ");
                if (!string.IsNullOrWhiteSpace(row.WindDirection))
                {
                    sb.Append(row.WindDirection).Append(' ');
                }

                sb.Append(Math.Round(wind).ToString("0", CultureInfo.InvariantCulture)).Append(' ').Append(windUnit);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static WeatherHourly? MatchHour(WeatherSnapshot? snapshot, DateOnly date, int hour)
    {
        if (snapshot is null)
        {
            return null;
        }

        var tz = WeatherLineupHelper.GetScheduleTimeZone();
        return snapshot.Hourly.FirstOrDefault(row =>
        {
            var local = TimeZoneInfo.ConvertTime(row.Time, tz);
            return DateOnly.FromDateTime(local.DateTime) == date && local.Hour == hour;
        });
    }

    private static string HourCondition(WeatherHourly? hour, WeatherSnapshot? snapshot)
    {
        if (!string.IsNullOrWhiteSpace(hour?.ConditionText))
        {
            return hour.ConditionText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(hour?.IconKey))
        {
            return WeatherIconMap.DisplayName(hour.IconKey);
        }

        if (!string.IsNullOrWhiteSpace(snapshot?.Current?.ConditionText))
        {
            return snapshot.Current.ConditionText.Trim();
        }

        return "Local weather";
    }

    private static string? FormatTemperature(double? value, bool useMetric)
    {
        if (value is not double number)
        {
            return null;
        }

        return Math.Round(number).ToString("0", CultureInfo.InvariantCulture) + (useMetric ? "°C" : "°F");
    }

    private static List<string> DefaultCategories(WeatherSnapshot? snapshot)
    {
        var categories = new List<string> { "Weather", "News" };
        if (snapshot?.Alerts is { Count: > 0 })
        {
            categories.Add("Weather Warning");
        }

        return categories;
    }

    private static DateOnly LocalForecastDate(TimeZoneInfo tz)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

    public static DateTimeOffset NextLocalMidnight(TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? WeatherLineupHelper.GetScheduleTimeZone();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var nextLocal = DateTime.SpecifyKind(localNow.Date.AddDays(1), DateTimeKind.Unspecified);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal, tz);
        return new DateTimeOffset(nextUtc, TimeSpan.Zero);
    }

    private static void PruneStaleCache(DateOnly forecastDate)
    {
        var keepFrom = forecastDate.AddDays(-1);
        WeatherGuideCacheStore.Prune(key =>
        {
            var parts = key.Split('|');
            if (parts.Length < 4)
            {
                return true;
            }

            return !DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < keepFrom;
        });
    }

    private static string FormatHourLabel(int hour)
    {
        var date = DateTime.Today.AddHours(hour);
        return date.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private static bool IsAiConfigured()
    {
        var ai = FinTvRuntime.Current?.Configuration.Ai;
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

        var zip = ExtractZip(query);
        if (!string.IsNullOrWhiteSpace(zip))
        {
            return zip;
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

    public static string? ExtractZip(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var match = Regex.Match(query, @"\b(\d{5})(?:-\d{4})?\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string NormalizeZip(string? zip) => NormalizeLocation(zip);

    public static string NormalizeLocation(string? location)
    {
        var trimmed = (location ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            throw new ArgumentException("Enter a US ZIP, city, or latitude,longitude.");
        }

        var zipOnly = Regex.Match(trimmed, @"^(\d{5})(?:-\d{4})?$");
        if (zipOnly.Success)
        {
            return zipOnly.Groups[1].Value;
        }

        return trimmed;
    }

    private static bool LooksNumeric(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}
