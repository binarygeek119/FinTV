using ChannelFlow.CommercialDetect;

namespace FinTv.Services;

/// <summary>
/// Midnight commercial-break scan for videos that still have no probe. Uses the same detector as Commercial Spot Tester.
/// </summary>
public sealed class CatalogCommercialBreakScanService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CatalogCommercialBreakScanService> _logger;
    private readonly object _gate = new();
    private bool _running;
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastCompletedAt;
    private string? _lastError;
    private int _lastProbed;
    private int _lastAdded;
    private int _lastSkipped;
    private int _lastFailed;
    private int _lastWroteFiles;
    private string? _lastSkipExample;

    private int _processed;
    private int _total;
    private int _found;
    private string? _currentFile;
    private string? _phase;
    private double _fileFraction;

    public CatalogCommercialBreakScanService(
        IServiceScopeFactory scopes,
        ILogger<CatalogCommercialBreakScanService> logger)
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
        var settings = CurrentSettings();
        lock (_gate)
        {
            return new
            {
                isRunning = _running,
                scanEnabled = settings.ScanEnabled,
                writeChaptersToFiles = settings.WriteChaptersToFiles,
                nextRunAt = CatalogFfprobeScanService.NextMidnight(DateTimeOffset.Now),
                lastStartedAt = _lastStartedAt,
                lastCompletedAt = _lastCompletedAt,
                lastError = _lastError,
                lastProbed = _lastProbed,
                lastAdded = _lastAdded,
                lastSkipped = _lastSkipped,
                lastFailed = _lastFailed,
                lastWroteFiles = _lastWroteFiles,
                skipExample = _lastSkipExample,
                processed = _processed,
                total = _total,
                found = _found,
                currentFile = _currentFile,
                phase = _phase,
                percent = OverallPercent(),
                message = RunningMessage(settings),
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
            _currentFile = null;
            _phase = null;
            _fileFraction = 0;
            return true;
        }
    }

    public Task RunMissingAsync(CancellationToken cancellationToken)
        => RunMissingAsync(begin: true, cancellationToken);

    public async Task RunMissingAsync(bool begin, CancellationToken cancellationToken)
    {
        var settings = CurrentSettings();
        if (!settings.ScanEnabled)
        {
            _logger.LogInformation("Commercial-break scan is off; skipping.");
            if (!begin)
            {
                lock (_gate)
                {
                    _running = false;
                    _lastCompletedAt = DateTimeOffset.UtcNow;
                }
            }

            return;
        }

        if (begin && !TryBegin())
        {
            return;
        }

        var probed = 0;
        var added = 0;
        var skipped = 0;
        var failed = 0;
        var wroteFiles = 0;
        string? error = null;
        try
        {
            using var scope = _scopes.CreateScope();
            var probe = scope.ServiceProvider.GetRequiredService<CatalogCommercialBreakProbeService>();
            var result = await probe.ProbeAsync(
                    settings,
                    missingOnly: true,
                    onProgress: update =>
                    {
                        lock (_gate)
                        {
                            _processed = update.Processed;
                            _total = update.Total;
                            _found = update.Found;
                            _currentFile = update.FileName;
                            _phase = update.Phase;
                            _fileFraction = update.FileFraction;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            probed = result.Probed;
            added = result.Added;
            skipped = result.Skipped;
            failed = result.Failed;
            wroteFiles = result.WroteFiles;
            lock (_gate)
            {
                _lastSkipExample = result.SkipExample;
            }
            _logger.LogInformation(
                "Commercial-break missing-info scan probed {Probed} videos, added {Added} breaks, wrote {Wrote} files, skipped {Skipped}, failed {Failed}",
                probed,
                added,
                wroteFiles,
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
            _logger.LogWarning(ex, "Commercial-break missing-info scan failed");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _lastCompletedAt = DateTimeOffset.UtcNow;
                _lastProbed = probed;
                _lastAdded = added;
                _lastSkipped = skipped;
                _lastFailed = failed;
                _lastWroteFiles = wroteFiles;
                _currentFile = null;
                _phase = null;
                _fileFraction = 0;
                if (error is not null)
                {
                    _lastError = error;
                }
            }
        }
    }

    private static CommercialBreakScanSettings CurrentSettings()
    {
        var settings = FinTvRuntime.Current?.Configuration.CommercialBreakScan ?? new CommercialBreakScanSettings();
        settings.Clamp();
        return settings;
    }

    private string RunningMessage(CommercialBreakScanSettings settings)
    {
        if (!_running)
        {
            if (!settings.ScanEnabled)
            {
                return "Scan commercial breaks is off. Enable it, then Run now or wait for midnight.";
            }

            if (_lastError is not null)
            {
                return _lastError;
            }

            if (_lastCompletedAt is not null)
            {
                var summary = "Probed " + _lastProbed + " video(s), added " + _lastAdded + " break chapter(s)"
                    + (_lastWroteFiles > 0 ? ", wrote " + _lastWroteFiles + " file(s)" : "")
                    + ", skipped " + _lastSkipped + ".";
                if (_lastProbed == 0 && _lastSkipped > 0)
                {
                    summary += " Remapped files were not on disk"
                        + (_lastSkipExample is null ? "." : " (example: " + _lastSkipExample + ").")
                        + " Check Library path remaps.";
                }

                return summary;
            }

            return "Waiting for the midnight commercial-break scan.";
        }

        if (_total <= 0)
        {
            return "Looking for videos that still have no commercial-break scan…";
        }

        var count = _processed + " of " + _total;
        var found = _found == 0 ? "" : ", " + _found + " breaks";
        if (!string.IsNullOrWhiteSpace(_currentFile))
        {
            var phase = string.IsNullOrWhiteSpace(_phase) ? "Scanning" : _phase;
            var pct = (int)Math.Round(100.0 * Math.Clamp(_fileFraction, 0, 1));
            return phase + " " + _currentFile + " (" + count + found + ", file " + pct + "%)…";
        }

        return "Scanning commercial breaks (" + count + found + ")…";
    }

    private int? OverallPercent()
    {
        if (_total <= 0)
        {
            return null;
        }

        var fraction = (_processed + Math.Clamp(_fileFraction, 0, 1)) / _total;
        return Math.Clamp((int)Math.Round(100.0 * fraction), 0, 100);
    }
}
