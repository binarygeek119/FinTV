using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Deletes ChannelFlow log files whose date is 8 calendar days old or older.
/// Startup already purges once; this catches long-running processes after local midnight.
/// </summary>
public sealed class LogRetentionHostedService : BackgroundService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LogRetentionHostedService> _logger;

    public LogRetentionHostedService(IWebHostEnvironment env, ILogger<LogRetentionHostedService> logger)
    {
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextPurge(DateTime.Now);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var logsDir = FileLogging.ResolveDirectory(_env.ContentRootPath);
                var purged = FileLogging.PurgeExpiredLogs(logsDir, DateTime.Today);
                if (purged > 0)
                {
                    _logger.LogInformation(
                        "Removed {Count} ChannelFlow log file(s) older than {Days} days from {LogDirectory}",
                        purged,
                        FileLogging.KeptPreviousCalendarDays,
                        logsDir);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ChannelFlow log retention purge failed");
            }
        }
    }

    internal static TimeSpan DelayUntilNextPurge(DateTime now)
    {
        var next = now.Date.AddDays(1).AddMinutes(1);
        var delay = next - now;
        return delay < TimeSpan.FromSeconds(5) ? TimeSpan.FromMinutes(1) : delay;
    }
}
