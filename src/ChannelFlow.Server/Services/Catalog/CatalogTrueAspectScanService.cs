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
    private string? _lastSkipExample;

    private int _processed;
    private int _total;
    private int _found;

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
                lastFailed = _lastFailed,
                skipExample = _lastSkipExample,
                processed = _processed,
                total = _total,
                found = _found,
                percent = _total > 0 ? Math.Clamp((int)Math.Round(100.0 * _processed / _total), 0, 100) : (int?)null,
                message = RunningMessage(),
                finishedAt = _lastCompletedAt
            };
        }
    }

    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _lastStartedAt = DateTimeOffset.UtcNow;
            _lastError = null;
            _lastSkipExample = null;
            _processed = 0;
            _total = 0;
            _found = 0;
            return true;
        }
    }

    public Task RunMissingAsync(CancellationToken cancellationToken)
        => RunMissingAsync(begin: true, cancellationToken);

    public async Task RunMissingAsync(bool begin, CancellationToken cancellationToken)
    {
        if (begin && !TryBegin())
        {
            return;
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
                    onProgress: (done, total, found) =>
                    {
                        lock (_gate)
                        {
                            _processed = done;
                            _total = total;
                            _found = found;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            probed = result.Probed;
            measured = result.Measured;
            skipped = result.Skipped;
            failed = result.Failed;
            lock (_gate)
            {
                _lastSkipExample = result.SkipExample;
            }
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

    private string RunningMessage()
    {
        if (!_running)
        {
            if (_lastError is not null)
            {
                return _lastError;
            }

            if (_lastCompletedAt is not null)
            {
                var summary = "Measured " + _lastMeasured + " of " + _lastProbed + " video(s), skipped " + _lastSkipped + ".";
                if (_lastProbed == 0 && _lastSkipped > 0)
                {
                    summary += " Remapped files were not on disk"
                        + (_lastSkipExample is null ? "." : " (example: " + _lastSkipExample + ").")
                        + " Check Library path remaps.";
                }

                return summary;
            }

            return "Waiting for the midnight true-aspect scan.";
        }

        if (_total <= 0)
        {
            return "Looking for videos that still have no TrueAspectRatio…";
        }

        return _found == 0
            ? "Sampling active picture (" + _processed + " of " + _total + ")…"
            : "Sampling active picture (" + _processed + " of " + _total + ", " + _found + " measured)…";
    }
}
