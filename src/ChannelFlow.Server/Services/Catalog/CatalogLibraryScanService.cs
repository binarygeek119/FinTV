using FinTv.Data;
using FinTv.News;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

/// <summary>
/// Syncs every enabled library connection so new files land in the catalog.
/// Runs every 6 hours (00:00, 06:00, 12:00, 18:00 local).
/// </summary>
public sealed class CatalogLibraryScanService
{
    public const int IntervalHours = 6;

    private readonly IServiceScopeFactory _scopes;
    private readonly CatalogSyncProgress _progress;
    private readonly ILogger<CatalogLibraryScanService> _logger;
    private readonly object _gate = new();
    private bool _running;
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastCompletedAt;
    private string? _lastError;
    private int _lastSynced;
    private int _lastSkipped;
    private int _lastImported;

    public CatalogLibraryScanService(
        IServiceScopeFactory scopes,
        CatalogSyncProgress progress,
        ILogger<CatalogLibraryScanService> logger)
    {
        _scopes = scopes;
        _progress = progress;
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
                intervalHours = IntervalHours,
                nextRunAt = NewsBulletinService.NextSixHourMark(DateTimeOffset.Now),
                lastStartedAt = _lastStartedAt,
                lastCompletedAt = _lastCompletedAt,
                lastError = _lastError,
                lastSynced = _lastSynced,
                lastSkipped = _lastSkipped,
                lastImported = _lastImported
            };
        }
    }

    public async Task RunAllAsync(CancellationToken cancellationToken)
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

        var synced = 0;
        var skipped = 0;
        var imported = 0;
        string? error = null;
        try
        {
            List<(Guid Id, string Name)> connections;
            using (var scope = _scopes.CreateScope())
            {
                var servers = scope.ServiceProvider.GetRequiredService<MediaServerService>();
                connections = await servers.ListEnabledSyncableAsync(cancellationToken).ConfigureAwait(false);
            }

            skipped = await CountEnabledUnsyncableAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (id, name) in connections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForIdleSyncAsync(cancellationToken).ConfigureAwait(false);
                using var scope = _scopes.CreateScope();
                var servers = scope.ServiceProvider.GetRequiredService<MediaServerService>();
                try
                {
                    var result = await servers.SyncAsync(id, cancellationToken).ConfigureAwait(false);
                    imported += result.Count;
                    synced++;
                    _logger.LogInformation("Scheduled library scan imported {Count} items from {Server}", result.Count, name);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    _logger.LogInformation("Scheduled library scan skipped {Server}: a catalog sync is already running", name);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not available", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = name + ": " + ex.Message;
                    _logger.LogWarning(ex, "Scheduled library scan failed for {Server}", name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Scheduled library scan failed");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _lastCompletedAt = DateTimeOffset.UtcNow;
                _lastSynced = synced;
                _lastSkipped = skipped;
                _lastImported = imported;
                if (error is not null)
                {
                    _lastError = error;
                }
            }
        }
    }

    private async Task WaitForIdleSyncAsync(CancellationToken cancellationToken)
    {
        while (_progress.IsRunning)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> CountEnabledUnsyncableAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var servers = scope.ServiceProvider.GetRequiredService<MediaServerService>();
        var enabled = await db.MediaServerConnections.AsNoTracking()
            .CountAsync(c => c.Enabled, cancellationToken)
            .ConfigureAwait(false);
        var syncable = (await servers.ListEnabledSyncableAsync(cancellationToken).ConfigureAwait(false)).Count;
        return Math.Max(0, enabled - syncable);
    }
}
