using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// One shared MPEG-TS encoder for a channel, fanned out to every current viewer.
/// After the last viewer leaves, encoding continues until the configured idle timeout.
/// </summary>
internal sealed class ChannelLiveSession
{
    private readonly Guid _channelId;
    private readonly Func<Stream, CancellationToken, Task> _encode;
    private readonly Action<ChannelLiveSession> _onStopped;
    private readonly ILogger _logger;
    private readonly FanoutSink _sink;
    private readonly object _gate = new();
    private readonly List<Viewer> _viewers = new();
    private readonly Queue<byte[]> _replay = new();

    private CancellationTokenSource? _encodeCts;
    private CancellationTokenSource? _idleCts;
    private Task? _encodeTask;
    private Exception? _encodeError;
    private bool _stopped;
    private long _replayBytes;
    private long _pacedBytes;
    private Stopwatch? _paceClock;
    private int _runAheadOverrideSeconds = -1;

    public ChannelLiveSession(
        Guid channelId,
        Func<Stream, CancellationToken, Task> encode,
        Action<ChannelLiveSession> onStopped,
        ILogger logger)
    {
        _channelId = channelId;
        _encode = encode;
        _onStopped = onStopped;
        _logger = logger;
        _sink = new FanoutSink(this);
    }

    public async Task<bool> AttachViewerAsync(Stream output, CancellationToken cancellationToken)
    {
        var viewer = new Viewer();
        if (!TryBeginAttach(viewer, out var primed))
        {
            return false;
        }

        try
        {
            foreach (var chunk in primed)
            {
                await output.WriteAsync(chunk, cancellationToken);
            }

            await viewer.PumpAsync(output, cancellationToken);
            ThrowIfEncodeFailed();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        finally
        {
            EndAttach(viewer);
        }
    }

    public void ForceStop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    /// <summary>
    /// Drops already-encoded video so a newly spliced commercial is not stuck behind the run-ahead buffer.
    /// </summary>
    public void DropReplayAndResetPace()
    {
        lock (_gate)
        {
            ClearBufferedVideoLocked(resetPace: true);
        }
    }

    /// <summary>
    /// Drops the run-ahead ring and unread tuner data, then paces at
    /// <paramref name="runAheadSeconds"/> so an EBS alert is not stuck behind the full buffer.
    /// </summary>
    public void FlushForWeatherAlert(int runAheadSeconds)
    {
        lock (_gate)
        {
            ClearBufferedVideoLocked(resetPace: true);
            foreach (var viewer in _viewers)
            {
                viewer.DropPending();
            }

            _runAheadOverrideSeconds = Math.Max(0, runAheadSeconds);
        }

        _logger.LogInformation(
            "Flushed run-ahead buffer on channel {ChannelId} to {RunAheadSeconds}s for a weather alert",
            _channelId,
            Math.Max(0, runAheadSeconds));
    }

    /// <summary>
    /// Lets the encoder fill back to the configured run-ahead after an EBS alert.
    /// </summary>
    public void RestoreRunAhead()
    {
        lock (_gate)
        {
            _runAheadOverrideSeconds = -1;
        }

        _logger.LogInformation(
            "Restoring {RunAheadSeconds}s run-ahead buffer on channel {ChannelId} after a weather alert",
            StreamService.GetRunAheadSeconds(),
            _channelId);
    }

    private void ClearBufferedVideoLocked(bool resetPace)
    {
        _replay.Clear();
        _replayBytes = 0;
        if (resetPace)
        {
            _pacedBytes = 0;
            _paceClock = null;
        }
    }

    private bool TryBeginAttach(Viewer viewer, out byte[][] primed)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                primed = [];
                return false;
            }

            CancelIdleLocked();
            primed = _replay.ToArray();
            _viewers.Add(viewer);
            EnsureEncodeLocked();
            return true;
        }
    }

    private void EndAttach(Viewer viewer)
    {
        var startIdle = false;
        lock (_gate)
        {
            _viewers.Remove(viewer);
            viewer.Complete();
            if (!_stopped && _viewers.Count == 0)
            {
                startIdle = true;
            }
        }

        if (startIdle)
        {
            ScheduleIdleStop();
        }
    }

    private void EnsureEncodeLocked()
    {
        if (_encodeTask is not null)
        {
            return;
        }

        _encodeCts = new CancellationTokenSource();
        var token = _encodeCts.Token;
        _pacedBytes = 0;
        _paceClock = null;
        var runAhead = StreamService.GetRunAheadSeconds();
        _logger.LogInformation(
            "Starting shared encoder for channel {ChannelId} with {RunAheadSeconds}s run-ahead buffer",
            _channelId,
            runAhead);
        _encodeTask = Task.Run(async () =>
        {
            try
            {
                await _encode(_sink, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _encodeError = ex;
                _logger.LogError(ex, "Shared encoder failed for channel {ChannelId}", _channelId);
            }
            finally
            {
                lock (_gate)
                {
                    StopLocked();
                }
            }
        }, CancellationToken.None);
    }

    private void ScheduleIdleStop()
    {
        var seconds = StreamService.GetIdleTimeoutSeconds();
        CancellationToken token;
        lock (_gate)
        {
            if (_stopped || _viewers.Count > 0)
            {
                return;
            }

            CancelIdleLocked();
            if (seconds <= 0)
            {
                _logger.LogInformation("Stopping encoder for channel {ChannelId}; no viewers and idle timeout is 0", _channelId);
                StopLocked();
                return;
            }

            _idleCts = new CancellationTokenSource();
            token = _idleCts.Token;
        }

        _logger.LogInformation(
            "Keeping encoder alive for {IdleSeconds}s on channel {ChannelId} with no viewers",
            seconds,
            _channelId);
        _ = WaitAndStopAsync(seconds, token);
    }

    private async Task WaitAndStopAsync(int seconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopped || _viewers.Count > 0)
            {
                return;
            }

            _logger.LogInformation("Idle timeout elapsed; stopping encoder for channel {ChannelId}", _channelId);
            StopLocked();
        }
    }

    private void CancelIdleLocked()
    {
        if (_idleCts is null)
        {
            return;
        }

        try
        {
            _idleCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _idleCts.Dispose();
        _idleCts = null;
    }

    private void StopLocked()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        CancelIdleLocked();
        try
        {
            _encodeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var viewer in _viewers)
        {
            viewer.Complete();
        }

        _onStopped(this);
    }

    private void ThrowIfEncodeFailed()
    {
        if (_encodeError is { } error)
        {
            throw error;
        }
    }

    private async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        await PaceEncoderAsync(data.Length, cancellationToken);

        var copy = data.ToArray();
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _replay.Enqueue(copy);
            _replayBytes += copy.Length;
            TrimReplayLocked();
            foreach (var viewer in _viewers)
            {
                viewer.TryWrite(copy);
            }
        }
    }

    private async Task PaceEncoderAsync(int byteCount, CancellationToken cancellationToken)
    {
        double extra;
        lock (_gate)
        {
            var runAhead = EffectiveRunAheadSecondsLocked();
            _pacedBytes += byteCount;
            _paceClock ??= Stopwatch.StartNew();
            var mediaSeconds = _pacedBytes / (double)StreamService.RunAheadBytesPerSecond;
            var allowed = _paceClock.Elapsed.TotalSeconds + runAhead;
            extra = mediaSeconds - allowed;
        }

        if (extra > 0.02)
        {
            await Task.Delay(TimeSpan.FromSeconds(extra), cancellationToken);
        }
    }

    private int EffectiveRunAheadSecondsLocked()
        => _runAheadOverrideSeconds >= 0
            ? _runAheadOverrideSeconds
            : StreamService.GetRunAheadSeconds();

    private void TrimReplayLocked()
    {
        var maxBytes = Math.Max(1, EffectiveRunAheadSecondsLocked()) * StreamService.RunAheadBytesPerSecond;
        while (_replayBytes > maxBytes && _replay.Count > 1)
        {
            var old = _replay.Dequeue();
            _replayBytes -= old.Length;
        }
    }

    private sealed class Viewer
    {
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8192)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        public void TryWrite(byte[] chunk)
        {
            _queue.Writer.TryWrite(chunk);
        }

        public void DropPending()
        {
            while (_queue.Reader.TryRead(out _))
            {
            }
        }

        public void Complete()
        {
            _queue.Writer.TryComplete();
        }

        public async Task PumpAsync(Stream output, CancellationToken cancellationToken)
        {
            await foreach (var chunk in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                await output.WriteAsync(chunk, cancellationToken);
            }
        }
    }

    private sealed class FanoutSink : Stream
    {
        private readonly ChannelLiveSession _session;

        public FanoutSink(ChannelLiveSession session)
        {
            _session = session;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _session.BroadcastAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _session.BroadcastAsync(buffer.ToArray(), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _session.BroadcastAsync(buffer, cancellationToken);
        }
    }
}
