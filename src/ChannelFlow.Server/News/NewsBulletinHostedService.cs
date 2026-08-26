using FinTv.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsBulletinHostedService : BackgroundService
{
    private readonly NewsBulletinService _bulletins;
    private readonly DatabaseInitializer _database;
    private readonly ILogger<NewsBulletinHostedService> _logger;

    public NewsBulletinHostedService(
        NewsBulletinService bulletins,
        DatabaseInitializer database,
        ILogger<NewsBulletinHostedService> logger)
    {
        _bulletins = bulletins;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _database.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _bulletins.SweepNow();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "News leftover cleanup at startup failed");
        }

        try
        {
            if (_bulletins.ShouldRetryFailedEncode())
            {
                _logger.LogInformation("Retrying news video after the last FFmpeg failure");
                var retry = await _bulletins.RunAsync(scheduled: false, required: true, stoppingToken);
                if (retry.Created)
                {
                    _logger.LogInformation("Retry news video written to {Path}", retry.Path);
                }
                else
                {
                    _logger.LogWarning("Retry news video failed: {Reason}", retry.SkipReason);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "News video retry at startup failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NewsBulletinService.NextSixHourMark(DateTimeOffset.Now);
            var delay = next - DateTimeOffset.Now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            _logger.LogInformation("Next news video at {When} (in {Delay})", next, delay);
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
                var result = await _bulletins.RunAsync(scheduled: true, stoppingToken);
                if (result.Created)
                {
                    _logger.LogInformation("Scheduled news video written to {Path}", result.Path);
                }
                else
                {
                    _logger.LogInformation("Scheduled news video skipped: {Reason}", result.SkipReason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled news video failed");
            }
        }
    }
}
