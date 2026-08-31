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
    private readonly ClientLogStore _clientLogs;
    private readonly ILogger<LogRetentionHostedService> _logger;

    public LogRetentionHostedService(
        IWebHostEnvironment env,
        ClientLogStore clientLogs,
        ILogger<LogRetentionHostedService> logger)
    {
        _env = env;
        _clientLogs = clientLogs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TryPurge();
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

            TryPurge();
        }
    }

    private void TryPurge()
    {
        try
        {
            var logsDir = FileLogging.ResolveDirectory(_env.ContentRootPath);
            var purged = FileLogging.PurgeExpiredLogs(logsDir, DateTime.Today);
            var purgedClients = _clientLogs.PurgeExpired(DateTime.Today);
            if (purged > 0)
            {
                _logger.LogInformation(
                    "Removed {Count} ChannelFlow log file(s) older than {Days} days from {LogDirectory}",
                    purged,
                    FileLogging.KeptPreviousCalendarDays,
                    logsDir);
            }

            if (purgedClients > 0)
            {
                _logger.LogInformation(
                    "Removed {Count} ChannelFlow client log file(s) older than {Days} days",
                    purgedClients,
                    FileLogging.KeptPreviousCalendarDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ChannelFlow log retention purge failed");
        }
    }

    internal static TimeSpan DelayUntilNextPurge(DateTime now)
    {
        var next = now.Date.AddDays(1).AddMinutes(1);
        var delay = next - now;
        return delay < TimeSpan.FromSeconds(5) ? TimeSpan.FromMinutes(1) : delay;
    }
}
