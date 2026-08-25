using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Downloads the Anytime music pack on startup when it is missing and a Drive file is configured.
/// </summary>
public sealed class MusicPackStartupHostedService : IHostedService
{
    private readonly MusicPackService _packs;
    private readonly ILogger<MusicPackStartupHostedService> _logger;

    public MusicPackStartupHostedService(MusicPackService packs, ILogger<MusicPackStartupHostedService> logger)
    {
        _packs = packs;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = DownloadAnytimeAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task DownloadAnytimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _packs.EnsureAnytimeDownloadedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anytime music pack auto-download did not finish");
        }
    }
}
