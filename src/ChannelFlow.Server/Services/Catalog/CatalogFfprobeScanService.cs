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
                    onProgress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            probed = result.Probed;
            withChapters = result.WithChapters;
            skipped = result.Skipped;
            failed = result.Failed;
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
}
