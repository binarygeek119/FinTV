namespace FinTv.Services;

public sealed class CatalogSyncProgress
{
    private readonly object _gate = new();
    private State _state = State.Idle();

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _state.Running;
            }
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            var total = _state.Total;
            var current = _state.Phase is "saving" or "finishing" or "chapters" or "done" ? _state.Saved : _state.Items;
            int? percent = null;
            if (total > 0)
            {
                percent = Math.Clamp((int)Math.Round(100.0 * current / total), 0, 100);
            }
            else if (_state.Phase == "fetching" && _state.LibraryCount > 0)
            {
                percent = Math.Clamp(
                    (int)Math.Round(100.0 * Math.Max(0, _state.LibraryIndex - 1) / _state.LibraryCount),
                    0,
                    99);
            }
            return new
            {
                running = _state.Running,
                phase = _state.Phase,
                serverName = _state.ServerName,
                libraryName = _state.LibraryName,
                libraryIndex = _state.LibraryIndex,
                libraryCount = _state.LibraryCount,
                items = _state.Items,
                saved = _state.Saved,
                total,
                percent,
                message = _state.Message,
                error = _state.Error,
                startedAt = _state.StartedAt,
                finishedAt = _state.FinishedAt
            };
        }
    }

    public bool TryStart(string serverName)
    {
        lock (_gate)
        {
            if (_state.Running)
            {
                return false;
            }

            _state = new State
            {
                Running = true,
                Phase = "starting",
                ServerName = serverName,
                Message = "Starting catalog sync…",
                StartedAt = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public void Libraries(string serverName, int count)
        => Set(s =>
        {
            s.ServerName = serverName;
            s.Phase = "libraries";
            s.LibraryCount = count;
            s.Message = count == 1
                ? "Syncing 1 library…"
                : count > 1
                    ? "Syncing " + count + " libraries…"
                    : "Preparing libraries…";
        });

    public void Fetching(string libraryName, int libraryIndex, int libraryCount, int items, int total)
        => Set(s =>
        {
            s.Phase = "fetching";
            s.LibraryName = libraryName;
            s.LibraryIndex = libraryIndex;
            s.LibraryCount = libraryCount;
            s.Items = items;
            s.Total = total > 0 ? Math.Max(total, items) : 0;
            s.Message = string.IsNullOrWhiteSpace(libraryName)
                ? "Fetching items…"
                : "Fetching " + libraryName + "…";
        });

    public void Saving(int saved, int total)
        => Set(s =>
        {
            s.Phase = "saving";
            s.Saved = saved;
            s.Total = Math.Max(total, saved);
            s.Items = Math.Max(s.Items, total);
            s.Message = "Writing catalog…";
        });

    public void Finishing(int count)
        => Set(s =>
        {
            s.Phase = "finishing";
            s.Saved = count;
            s.Items = Math.Max(s.Items, count);
            s.Total = Math.Max(s.Total, count);
            s.Message = "Marking removed items…";
        });

    public void Probing(int processed, int total, int withChapters)
        => Set(s =>
        {
            s.Phase = "chapters";
            s.Saved = processed;
            s.Total = Math.Max(total, processed);
            s.Items = Math.Max(s.Items, total);
            s.Message = total == 0
                ? "No videos to probe for chapters."
                : withChapters == 0
                    ? "Reading chapters with ffprobe…"
                    : "Reading chapters with ffprobe (" + withChapters + " found)…";
        });

    public void Complete(int count)
        => Set(s =>
        {
            s.Running = false;
            s.Phase = "done";
            s.Saved = count;
            s.Items = Math.Max(s.Items, count);
            s.Total = Math.Max(s.Total, count);
            s.Message = count == 1 ? "Imported 1 item." : "Imported " + count + " items.";
            s.FinishedAt = DateTimeOffset.UtcNow;
        });

    public void Fail(string message)
        => Set(s =>
        {
            s.Running = false;
            s.Phase = "error";
            s.Error = message;
            s.Message = message;
            s.FinishedAt = DateTimeOffset.UtcNow;
        });

    private void Set(Action<State> update)
    {
        lock (_gate)
        {
            if (!_state.Running && _state.Phase is not "starting")
            {
                return;
            }

            update(_state);
        }
    }

    private sealed class State
    {
        public bool Running { get; set; }

        public string Phase { get; set; } = "idle";

        public string ServerName { get; set; } = "";

        public string? LibraryName { get; set; }

        public int LibraryIndex { get; set; }

        public int LibraryCount { get; set; }

        public int Items { get; set; }

        public int Saved { get; set; }

        public int Total { get; set; }

        public string Message { get; set; } = "";

        public string? Error { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public static State Idle() => new() { Phase = "idle", Message = "" };
    }
}
