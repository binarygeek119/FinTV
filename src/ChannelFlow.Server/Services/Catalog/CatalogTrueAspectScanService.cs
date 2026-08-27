namespace FinTv.Services;

/// <summary>
/// Midnight cropdetect pass for videos that still have no measured active-picture ratio.
/// </summary>
public sealed class CatalogTrueAspectScanService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CatalogTrueAspectScanService> _logger;
    private readonly object _gate = new();
    private bool _running;
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastCompletedAt;
    private string? _lastError;
    private int _lastProbed;
    private int _lastMeasured;
    private int _lastSkipped;
    private int _lastFailed;

    public CatalogTrueAspectScanService(
        IServiceScopeFactory scopes,
        ILogger<CatalogTrueAspectScanService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public object Describe()
    {
        lock (_gate)
        {
            return new
            {
                isRunning = _running,
                nextRunAt = CatalogFfprobeScanService.NextMidnight(DateTimeOffset.Now),
                lastStartedAt = _lastStartedAt,
                lastCompletedAt = _lastCompletedAt,
                lastError = _lastError,
                lastProbed = _lastProbed,
                lastMeasured = _lastMeasured,
                lastSkipped = _lastSkipped,
                lastFailed = _lastFailed
            };
        }
    }

    public async Task RunMissingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _lastStartedAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }

        var probed = 0;
        var measured = 0;
        var skipped = 0;
        var failed = 0;
        string? error = null;
        try
        {
            using var scope = _scopes.CreateScope();
            var probe = scope.ServiceProvider.GetRequiredService<CatalogTrueAspectProbeService>();
            var result = await probe.ProbeAsync(
                    itemIds: null,
                    missingOnly: true,
                    onProgress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            probed = result.Probed;
            measured = result.Measured;
            skipped = result.Skipped;
            failed = result.Failed;
            _logger.LogInformation(
                "True-aspect missing-info scan probed {Probed} videos, measured {Measured}, skipped {Skipped}, failed {Failed}",
                probed,
                measured,
                skipped,
                failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "True-aspect missing-info scan failed");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _lastCompletedAt = DateTimeOffset.UtcNow;
                _lastProbed = probed;
                _lastMeasured = measured;
                _lastSkipped = skipped;
                _lastFailed = failed;
                if (error is not null)
                {
                    _lastError = error;
                }
            }
        }
    }
}
