using System.Globalization;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Resolves ChannelFlow schedule time zones with normalization and safe fallbacks.
/// </summary>
public static class ScheduleTimeZoneHelper
{
    private const string DefaultTimeZoneId = "America/New_York";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US/Central"] = "America/Chicago",
        ["US/Eastern"] = "America/New_York",
        ["US/Mountain"] = "America/Denver",
        ["US/Pacific"] = "America/Los_Angeles",
        ["Central Standard Time"] = "America/Chicago",
        ["Eastern Standard Time"] = "America/New_York",
        ["Mountain Standard Time"] = "America/Denver",
        ["Pacific Standard Time"] = "America/Los_Angeles"
    };

    public static string GetConfiguredTimeZoneId()
        => NormalizeTimeZoneId(FinTvRuntime.Current?.Configuration.ScheduleTimeZone);

    /// <summary>
    /// Current instant in the schedule time zone (not container UTC).
    /// </summary>
    public static DateTimeOffset Now()
    {
        var tz = ResolveScheduleTimeZone();
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
    }

    /// <summary>
    /// Points the process local clock at the schedule time zone so logs and DateTime.Now
    /// follow ChannelFlow's setting instead of container UTC.
    /// </summary>
    public static void ApplyAsProcessTimeZone()
    {
        var tz = ResolveScheduleTimeZone();
        Environment.SetEnvironmentVariable("TZ", tz.Id);
        TimeZoneInfo.ClearCachedData();
    }

    public static string NormalizeTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return DefaultTimeZoneId;
        }

        var trimmed = timeZoneId.Trim();
        if (trimmed.StartsWith("American/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "America/" + trimmed["American/".Length..];
        }

        return Aliases.TryGetValue(trimmed, out var alias) ? alias : trimmed;
    }

    /// <summary>
    /// Floors a UTC instant to the top of the local hour in the schedule time zone.
    /// </summary>
    public static DateTime FloorToHourUtc(DateTime utc, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= ResolveScheduleTimeZone();
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
        var hourLocal = DateTime.SpecifyKind(
            new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
            DateTimeKind.Unspecified);
        return ConvertLocalToUtc(hourLocal, timeZone);
    }

    /// <summary>
    /// Adds local wall-clock hours (DST-safe) to a UTC instant.
    /// </summary>
    public static DateTime AddLocalHoursUtc(DateTime utc, int hours, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= ResolveScheduleTimeZone();
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
        var next = DateTime.SpecifyKind(local.AddHours(hours), DateTimeKind.Unspecified);
        return ConvertLocalToUtc(next, timeZone);
    }

    /// <summary>
    /// Rounds a UTC instant up to the next 30-minute clock time in the schedule time zone.
    /// Times already on :00 or :30 are unchanged.
    /// </summary>
    public static DateTime CeilToHalfHourUtc(DateTime utc, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= ResolveScheduleTimeZone();
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
        if (local.Second == 0 && local.Millisecond == 0 && (local.Minute == 0 || local.Minute == 30))
        {
            return utc;
        }

        var dayStart = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var elapsedMinutes = (int)Math.Floor((local - dayStart).TotalMinutes);
        var ceiledMinutes = ((elapsedMinutes / 30) + 1) * 30;
        var ceiledLocal = DateTime.SpecifyKind(dayStart.AddMinutes(ceiledMinutes), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(ceiledLocal, timeZone);
    }

    public static TimeZoneInfo ResolveScheduleTimeZone(ILogger? logger = null)
    {
        var configured = FinTvRuntime.Current?.Configuration.ScheduleTimeZone;
        foreach (var candidate in BuildCandidates(configured))
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var timeZone))
            {
                if (!string.Equals(NormalizeTimeZoneId(configured), candidate, StringComparison.Ordinal)
                    && logger is not null)
                {
                    logger.LogWarning(
                        "Schedule time zone {Configured} resolved as {Resolved}",
                        configured,
                        candidate);
                }

                return timeZone;
            }
        }

        logger?.LogWarning(
            "Schedule time zone {Configured} was not found; falling back to {Fallback}",
            configured ?? "(null)",
            DefaultTimeZoneId);

        return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
    }

    public static bool TryResolveScheduleTimeZone(string? timeZoneId, out TimeZoneInfo timeZone, out string resolvedId)
    {
        foreach (var candidate in BuildCandidates(timeZoneId))
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out timeZone!))
            {
                resolvedId = candidate;
                return true;
            }
        }

        timeZone = TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        resolvedId = DefaultTimeZoneId;
        return false;
    }

    public static IReadOnlyList<ScheduleTimeZoneOption> GetAvailableTimeZones()
    {
        var now = DateTime.UtcNow;
        return TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => new ScheduleTimeZoneOption
            {
                Id = tz.Id,
                Offset = FormatOffset(tz.GetUtcOffset(now)),
                Label = BuildLabel(tz.Id, tz.GetUtcOffset(now))
            })
            .OrderBy(tz => tz.Offset, StringComparer.Ordinal)
            .ThenBy(tz => tz.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string BuildLabel(string id, TimeSpan offset)
        => $"{id} ({FormatOffset(offset)})";

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var total = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"UTC{sign}{total.Hours:00}:{total.Minutes:00}");
    }

    private static DateTime ConvertLocalToUtc(DateTime localUnspecified, TimeZoneInfo timeZone)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timeZone);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.ConvertTimeToUtc(localUnspecified.AddHours(1), timeZone);
        }
    }

    private static IEnumerable<string> BuildCandidates(string? timeZoneId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (seen.Add(value))
            {
                list.Add(value);
            }
        }

        AddCandidate(NormalizeTimeZoneId(timeZoneId));
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            AddCandidate(timeZoneId.Trim());
        }

        AddCandidate(DefaultTimeZoneId);
        return list;
    }
}

/// <summary>
/// One selectable schedule time zone for the admin UI.
/// </summary>
public sealed class ScheduleTimeZoneOption
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Offset { get; init; } = string.Empty;
}
