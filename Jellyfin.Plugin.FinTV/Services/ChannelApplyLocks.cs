using System.Collections.Concurrent;

namespace Jellyfin.Plugin.FinTV.Services;

internal static class ChannelApplyLocks
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public static async Task<IDisposable> AcquireAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var gate = Locks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => _gate.Release();
    }
}
