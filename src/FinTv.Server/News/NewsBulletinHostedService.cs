using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsBulletinHostedService : BackgroundService
{
    private readonly NewsBulletinService _bulletins;
    private readonly ILogger<NewsBulletinHostedService> _logger;

    public NewsBulletinHostedService(NewsBulletinService bulletins, ILogger<NewsBulletinHostedService> logger)
    {
        _bulletins = bulletins;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _bulletins.SweepNow();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "News leftover cleanup at startup failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NewsBulletinService.NextSixHourMark(DateTimeOffset.Now);
            var delay = next - DateTimeOffset.Now;
            if (delay < TimeSpan.FromSeconds(5))
            {
                delay = TimeSpan.FromHours(NewsBulletinService.IntervalHours);
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
