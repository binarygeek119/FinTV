using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;

namespace FinTv;

/// <summary>
/// Serilog file logging for ChannelFlow-Server.
/// Active file (always, including across midnight): <c>channelflow-yyyyMMdd.log</c>.
/// On process restart, that day's previous active file is renamed to
/// <c>channelflow-yyyyMMdd.NN.log</c> with NN counting up from 01.
/// Retention keeps today plus the previous 7 calendar days (main + .NN archives);
/// files whose date is 8 days old or older are deleted.
/// </summary>
internal static class FileLogging
{
    internal const string ActiveFilePrefix = "channelflow-";
    internal const string LogExtension = ".log";
    internal const int KeptPreviousCalendarDays = 7;

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private static readonly Regex LogFileName = new(
        @"^channelflow-(?<date>\d{8})(?:\.(?<seq>\d+)|_(?<legacy>\d+))?\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string ResolveDirectory(string contentRoot)
    {
        var fromEnv = AppEnvironment.Get("LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var configDir = AppEnvironment.Get("CONFIG")
            ?? Path.Combine(contentRoot, "config");
        return Path.GetFullPath(Path.Combine(configDir, "logs"));
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        var logsDir = ResolveDirectory(builder.Environment.ContentRootPath);
        Directory.CreateDirectory(logsDir);

        var today = DateTime.Today;
        var rotated = RotateTodaysActiveFile(logsDir, today);
        var purged = PurgeExpiredLogs(logsDir, today);

        // Date token is inserted before the extension: channelflow-yyyyMMdd.log
        var logFile = Path.Combine(logsDir, "channelflow-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: null,
                rollOnFileSizeLimit: false,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: OutputTemplate)
            .CreateLogger();

        builder.Host.UseSerilog();
        Log.Information(
            "Clock utc={Utc:yyyy-MM-dd HH:mm:ss}Z TZ={TimeZone} local={Local:yyyy-MM-dd HH:mm:ss zzz} (TZ env {TzEnv})",
            DateTime.UtcNow,
            TimeZoneInfo.Local.Id,
            DateTimeOffset.Now,
            Environment.GetEnvironmentVariable("TZ") ?? "(unset)");
        Log.Information(
            "Writing logs to {LogDirectory} as {ActiveFile}",
            logsDir,
            ActiveFileName(today));
        if (rotated is not null)
        {
            Log.Information("Rotated previous run log to {ArchiveFile}", rotated);
        }

        if (purged > 0)
        {
            Log.Information("Removed {Count} ChannelFlow log file(s) older than {Days} days", purged, KeptPreviousCalendarDays);
        }
    }

    /// <summary>
    /// If today's unsuffixed file exists (previous process), rename it to the next
    /// free <c>channelflow-yyyyMMdd.NN.log</c>. Returns the archive file name, or null.
    /// </summary>
    internal static string? RotateTodaysActiveFile(string logsDir, DateTime today)
    {
        var dateStamp = DateStamp(today);
        var activePath = Path.Combine(logsDir, ActiveFileName(today));
        if (!File.Exists(activePath))
        {
            return null;
        }

        var next = HighestArchiveNumber(logsDir, dateStamp) + 1;
        while (next <= 9999)
        {
            var archiveName = ArchiveFileName(dateStamp, next);
            var archivePath = Path.Combine(logsDir, archiveName);
            if (File.Exists(archivePath))
            {
                next++;
                continue;
            }

            try
            {
                File.Move(activePath, archivePath);
                return archiveName;
            }
            catch (IOException) when (File.Exists(archivePath))
            {
                next++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"ChannelFlow: could not rotate {Path.GetFileName(activePath)} to {archiveName}: {ex.Message}. Appending to the existing file.");
                return null;
            }
        }

        Console.Error.WriteLine(
            $"ChannelFlow: could not rotate {Path.GetFileName(activePath)}; no free .NN archive slot.");
        return null;
    }

    /// <summary>
    /// Deletes main and archived log files whose calendar date is older than today plus
    /// the previous <see cref="KeptPreviousCalendarDays"/> days.
    /// </summary>
    internal static int PurgeExpiredLogs(string logsDir, DateTime today)
    {
        if (!Directory.Exists(logsDir))
        {
            return 0;
        }

        var oldestKeep = DateOnly.FromDateTime(today).AddDays(-KeptPreviousCalendarDays);
        var removed = 0;
        foreach (var path in Directory.GetFiles(logsDir, "channelflow-*.log"))
        {
            if (!TryParseLogDate(Path.GetFileName(path), out var fileDate) || fileDate >= oldestKeep)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ChannelFlow: could not delete expired log {path}: {ex.Message}");
            }
        }

        return removed;
    }

    internal static string ActiveFileName(DateTime day)
        => $"{ActiveFilePrefix}{DateStamp(day)}{LogExtension}";

    internal static string ArchiveFileName(string dateStamp, int sequence)
        => $"{ActiveFilePrefix}{dateStamp}.{sequence.ToString("D2", CultureInfo.InvariantCulture)}{LogExtension}";

    internal static bool TryParseLogDate(string fileName, out DateOnly date)
    {
        date = default;
        var match = LogFileName.Match(fileName);
        if (!match.Success)
        {
            return false;
        }

        return DateOnly.TryParseExact(
            match.Groups["date"].Value,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static string DateStamp(DateTime day)
        => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static int HighestArchiveNumber(string logsDir, string dateStamp)
    {
        var highest = 0;
        foreach (var path in Directory.GetFiles(logsDir, $"{ActiveFilePrefix}{dateStamp}.*.log"))
        {
            var match = LogFileName.Match(Path.GetFileName(path));
            if (!match.Success || !match.Groups["seq"].Success)
            {
                continue;
            }

            if (int.TryParse(match.Groups["seq"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                highest = Math.Max(highest, n);
            }
        }

        return highest;
    }
}
