namespace FinTv.Services;

/// <summary>
/// Midnight ffprobe pass for video files that still have no chapter probe data.
/// </summary>
public sealed class CatalogFfprobeScanService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CatalogFfprobeScanService> _logger;
    private readonly object _gate = new();
    private bool _running;
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastCompletedAt;
    private string? _lastError;
    private int _lastProbed;
    private int _lastWithChapters;
    private int _lastSkipped;
    private int _lastFailed;
    private string? _lastSkipExample;

    private int _processed;
    private int _total;
    private int _found;

    public CatalogFfprobeScanService(
        IServiceScopeFactory scopes,
        ILogger<CatalogFfprobeScanService> logger)
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

    public static DateTimeOffset NextMidnight(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var next = local.Date.AddDays(1);
        return new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), local.Offset);
    }

    public object Describe()
    {
        lock (_gate)
        {
            return new
            {
                isRunning = _running,
                nextRunAt = NextMidnight(DateTimeOffset.Now),
                lastStartedAt = _lastStartedAt,
                lastCompletedAt = _lastCompletedAt,
                lastError = _lastError,
                lastProbed = _lastProbed,
                lastWithChapters = _lastWithChapters,
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
        var withChapters = 0;
        var skipped = 0;
        var failed = 0;
        string? error = null;
        try
        {
            using var scope = _scopes.CreateScope();
            var probe = scope.ServiceProvider.GetRequiredService<CatalogChapterProbeService>();
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
            withChapters = result.WithChapters;
            skipped = result.Skipped;
            failed = result.Failed;
            lock (_gate)
            {
                _lastSkipExample = result.SkipExample;
            }
            _logger.LogInformation(
                "ffprobe missing-info scan probed {Probed} videos, {WithChapters} with chapters, {Skipped} skipped, {Failed} failed",
                probed,
                withChapters,
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
            _logger.LogWarning(ex, "ffprobe missing-info scan failed");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _lastCompletedAt = DateTimeOffset.UtcNow;
                _lastProbed = probed;
                _lastWithChapters = withChapters;
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
                var summary = "Probed " + _lastProbed + " video(s), found chapters on " + _lastWithChapters
                    + ", skipped " + _lastSkipped + ".";
                if (_lastProbed == 0 && _lastSkipped > 0)
                {
                    summary += " Remapped files were not on disk"
                        + (_lastSkipExample is null ? "." : " (example: " + _lastSkipExample + ").")
                        + " Check Library path remaps.";
                }

                return summary;
            }

            return "Waiting for the midnight chapter scan.";
        }

        if (_total <= 0)
        {
            return "Looking for videos that still have no chapter data…";
        }

        return _found == 0
            ? "Reading chapters with ffprobe (" + _processed + " of " + _total + ")…"
            : "Reading chapters with ffprobe (" + _processed + " of " + _total + ", " + _found + " found)…";
    }
}
