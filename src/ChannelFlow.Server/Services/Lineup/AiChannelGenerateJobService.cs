using System.Collections.Concurrent;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// FIFO AI generate queue: clicks stack, then one worker primes, builds lineups, and round-robins playout days.
/// </summary>
public sealed class AiChannelGenerateJobService
{
    private static readonly ConcurrentDictionary<Guid, JobState> Jobs = new();
    private static readonly object QueueLock = new();
    private static readonly List<Guid> Pending = new();
    private static CancellationTokenSource? WorkerCts;
    private static CancellationTokenSource? DebounceCts;
    private static int WorkerActive;
    private static QueueSnapshot Snapshot = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlayoutBuilderService _playoutBuilder;
    private readonly ILogger<AiChannelGenerateJobService> _logger;

    public AiChannelGenerateJobService(
        IServiceScopeFactory scopeFactory,
        PlayoutBuilderService playoutBuilder,
        ILogger<AiChannelGenerateJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _playoutBuilder = playoutBuilder;
        _logger = logger;
    }

    public bool IsRunning(Guid channelId)
    {
        lock (QueueLock)
        {
            if (Pending.Contains(channelId) || Snapshot.ChannelIds.Contains(channelId))
            {
                return Snapshot.IsRunning || Pending.Count > 0;
            }
        }

        if (!Jobs.TryGetValue(channelId, out var job))
        {
            return false;
        }

        lock (job)
        {
            return job.IsRunning;
        }
    }

    public bool TryQueue(Guid channelId, AiProvider? providerOverride)
    {
        _ = providerOverride;
        var job = Jobs.GetOrAdd(channelId, _ => new JobState());
        lock (job)
        {
            job.Error = null;
            job.ApplyError = null;
            job.Phase = "queued";
            job.IsRunning = true;
            job.StartedAtUtc = DateTime.UtcNow;
            job.CompletedAtUtc = null;
            job.TotalDays = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
        }

        lock (QueueLock)
        {
            if (!Pending.Contains(channelId))
            {
                Pending.Add(channelId);
            }

            if (Volatile.Read(ref WorkerActive) == 0 && !Snapshot.IsRunning)
            {
                Snapshot.Phase = "queued";
                Snapshot.Message = Pending.Count == 1
                    ? "Queued…"
                    : $"Queued {Pending.Count} channels…";
                Snapshot.CompletedAt = null;
                Snapshot.WasCancelled = false;
                Snapshot.LastError = null;
                Snapshot.CurrentDay = 0;
                Snapshot.CurrentChannelIndex = 0;
                Snapshot.ChannelName = null;
            }
        }

        _logger.LogInformation("Queued AI lineup generation for channel {ChannelId}", channelId);
        ScheduleDebounce();
        return true;
    }

    public bool IsQueueRunning()
    {
        lock (QueueLock)
        {
            return Snapshot.IsRunning || Pending.Count > 0 || Volatile.Read(ref WorkerActive) > 0;
        }
    }

    public void QueueGenerateAll()
    {
        SetSnapshot(s =>
        {
            s.IsRunning = true;
            s.Phase = "queuing";
            s.Message = "Queuing eligible channels…";
            s.WasCancelled = false;
            s.LastError = null;
            s.StartedAt = DateTime.UtcNow;
            s.CompletedAt = null;
        });
        EnqueueEligibleChannels();
    }

    public void EnqueueEligibleChannels()
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTv.Data.FinTvDbContext>();
            var rows = await db.Channels.AsNoTracking()
                .Where(c => c.Enabled && c.ContentType != ChannelContentType.Weather && c.ContentType != ChannelContentType.News)
                .OrderBy(c => c.Number)
                .ToListAsync();
            var queued = 0;
            foreach (var channel in rows.Where(AiChannelAutoApplyService.IsEligible))
            {
                TryQueue(channel.Id, null);
                queued++;
            }

            if (queued == 0)
            {
                SetSnapshot(s =>
                {
                    s.IsRunning = false;
                    s.Phase = "done";
                    s.Message = "No eligible channels.";
                    s.CompletedAt = DateTime.UtcNow;
                });
            }
        });
    }

    public bool Cancel()
    {
        lock (QueueLock)
        {
            Pending.Clear();
            WorkerCts?.Cancel();
            DebounceCts?.Cancel();
            Snapshot.WasCancelled = true;
            Snapshot.IsRunning = false;
            Snapshot.Phase = "cancelled";
        }

        foreach (var job in Jobs.Values)
        {
            lock (job)
            {
                if (job.IsRunning)
                {
                    job.IsRunning = false;
                    job.Error = "Cancelled.";
                    job.CompletedAtUtc = DateTime.UtcNow;
                }
            }
        }

        return true;
    }

    public object BuildStatus(Guid channelId)
    {
        var rebuild = _playoutBuilder.GetRebuildState(channelId);
        if (!Jobs.TryGetValue(channelId, out var job))
        {
            return new
            {
                channelId,
                isRunning = IsQueueBusy(),
                preview = (AiLineupPreviewResult?)null,
                applied = false,
                applyError = (string?)null,
                error = (string?)null,
                phase = Snapshot.Phase,
                currentDay = Snapshot.CurrentDay,
                totalDays = PlayoutScheduleHelper.GetPlayoutDaysToBuild(),
                channelName = Snapshot.ChannelName,
                rebuild
            };
        }

        lock (job)
        {
            return new
            {
                channelId,
                isRunning = job.IsRunning || IsQueueBusy() && Snapshot.ChannelIds.Contains(channelId),
                startedAt = job.StartedAtUtc,
                completedAt = job.CompletedAtUtc,
                preview = job.Preview,
                applied = job.Applied,
                applyError = job.ApplyError,
                error = job.Error,
                phase = job.Phase ?? Snapshot.Phase,
                currentDay = job.CurrentDay,
                totalDays = job.TotalDays,
                channelName = job.ChannelName ?? Snapshot.ChannelName,
                rebuild
            };
        }
    }

    public object BuildQueueStatus()
    {
        lock (QueueLock)
        {
            return new
            {
                isRunning = Snapshot.IsRunning || Pending.Count > 0 || Volatile.Read(ref WorkerActive) > 0,
                phase = Snapshot.Phase,
                channelName = Snapshot.ChannelName,
                currentDay = Snapshot.CurrentDay,
                totalDays = Snapshot.TotalDays,
                currentChannel = Snapshot.CurrentChannelIndex,
                totalChannels = Math.Max(Snapshot.ChannelIds.Count, Pending.Count),
                queued = Pending.Count,
                page = Snapshot.Page,
                totalPages = Snapshot.TotalPages,
                message = Snapshot.Message,
                lastError = Snapshot.LastError,
                wasCancelled = Snapshot.WasCancelled,
                startedAt = Snapshot.StartedAt,
                completedAt = Snapshot.CompletedAt
            };
        }
    }

    public object BuildGenerateAllStatus()
    {
        lock (QueueLock)
        {
            var totalChannels = Math.Max(Snapshot.ChannelIds.Count, Pending.Count);
            var totalDays = Snapshot.TotalDays > 0 ? Snapshot.TotalDays : PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            var totalSteps = Math.Max(1, totalChannels * totalDays);
            var channelIndex = Math.Max(Snapshot.CurrentChannelIndex, string.IsNullOrWhiteSpace(Snapshot.ChannelName) ? 0 : 1);
            var completedSteps = Snapshot.Phase is "done" or "cancelled" or "error"
                ? totalSteps
                : Snapshot.CurrentDay > 0
                    ? ((Snapshot.CurrentDay - 1) * totalChannels) + Math.Max(channelIndex, 1)
                    : 0;
            return new
            {
                isRunning = Snapshot.IsRunning || Pending.Count > 0 || Volatile.Read(ref WorkerActive) > 0,
                currentPhase = Snapshot.Phase,
                currentChannelName = Snapshot.ChannelName,
                currentDay = Snapshot.CurrentDay,
                totalDays,
                totalChannels,
                completedSteps,
                totalSteps,
                queued = Pending.Count,
                page = Snapshot.Page,
                totalPages = Snapshot.TotalPages,
                message = Snapshot.Message,
                lastError = Snapshot.LastError,
                wasCancelled = Snapshot.WasCancelled,
                startedAt = Snapshot.StartedAt,
                completedAt = Snapshot.CompletedAt,
                workerActive = Volatile.Read(ref WorkerActive) > 0,
                lineupsGenerated = totalChannels,
                playoutDaysBuilt = completedSteps
            };
        }
    }

    private bool IsQueueBusy()
    {
        lock (QueueLock)
        {
            return Snapshot.IsRunning || Pending.Count > 0;
        }
    }

    private void ScheduleDebounce()
    {
        DebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        DebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunWorkerAsync();
        });
    }

    private async Task RunWorkerAsync()
    {
        if (Interlocked.CompareExchange(ref WorkerActive, 1, 0) != 0)
        {
            return;
        }

        List<Guid> batch;
        lock (QueueLock)
        {
            batch = Pending.Distinct().ToList();
            Pending.Clear();
            Snapshot = new QueueSnapshot
            {
                IsRunning = true,
                ChannelIds = batch,
                TotalDays = PlayoutScheduleHelper.GetPlayoutDaysToBuild(),
                Phase = "starting",
                StartedAt = DateTime.UtcNow
            };
            WorkerCts = new CancellationTokenSource();
        }

        var cancel = WorkerCts!.Token;
        try
        {
            if (batch.Count == 0)
            {
                return;
            }

            using var primeScope = _scopeFactory.CreateScope();
            var db = primeScope.ServiceProvider.GetRequiredService<FinTv.Data.FinTvDbContext>();
            var channels = await db.Channels
                .Where(c => batch.Contains(c.Id))
                .OrderBy(c => c.Number)
                .ToListAsync(cancel);
            var pool = primeScope.ServiceProvider.GetRequiredService<ChannelCatalogPoolService>();
            SetSnapshot(s =>
            {
                s.Phase = "priming";
                s.Message = "Paging the live catalog to the AI…";
            });
            await pool.PrimeEmptyChannelsAsync(
                channels,
                (name, page, total) => SetSnapshot(s =>
                {
                    s.Phase = "priming";
                    s.ChannelName = name;
                    s.Page = page;
                    s.TotalPages = total;
                    s.Message = $"Priming {name}: page {page} of {total}";
                }),
                cancel);

            var days = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            var roundRobin = channels.Count > 1;
            foreach (var channel in channels)
            {
                cancel.ThrowIfCancellationRequested();
                SetSnapshot(s =>
                {
                    s.Phase = "lineup";
                    s.ChannelName = channel.Name;
                    s.Message = $"Generating weekly lineup for {channel.Name}";
                });
                using var lineupScope = _scopeFactory.CreateScope();
                var autoApply = lineupScope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                var result = await autoApply.GenerateAndBuildHorizonDaysAsync(
                    channel.Id,
                    null,
                    progress => UpdateJob(channel.Id, progress),
                    cancel,
                    skipIfAtHorizon: false,
                    buildPlayout: !roundRobin).ConfigureAwait(false);
                UpdateJobResult(channel.Id, result);
            }

            if (roundRobin)
            {
                for (var day = 0; day < days; day++)
                {
                    var channelIndex = 0;
                    foreach (var channel in channels)
                    {
                        cancel.ThrowIfCancellationRequested();
                        channelIndex++;
                        SetSnapshot(s =>
                        {
                            s.Phase = "playout";
                            s.ChannelName = channel.Name;
                            s.CurrentDay = day + 1;
                            s.TotalDays = days;
                            s.CurrentChannelIndex = channelIndex;
                            s.Message = $"Playout day {day + 1}/{days} · {channel.Name}";
                        });
                        using var dayScope = _scopeFactory.CreateScope();
                        var autoApply = dayScope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                        using (await ChannelApplyLocks.AcquireAsync(channel.Id, cancel))
                        {
                            await autoApply.BuildChannelPlayoutDayAsync(channel.Id, day, cancel, interruptStream: day == 0)
                                .ConfigureAwait(false);
                        }

                        UpdateJob(channel.Id, new AiChannelBuildProgress
                        {
                            CurrentDay = day + 1,
                            TotalDays = days,
                            Phase = "playout",
                            ChannelName = channel.Name
                        });
                    }
                }
            }

            foreach (var channel in channels)
            {
                if (Jobs.TryGetValue(channel.Id, out var job))
                {
                    lock (job)
                    {
                        job.IsRunning = false;
                        job.Phase = "done";
                        job.CompletedAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(s =>
            {
                s.WasCancelled = true;
                s.Phase = "cancelled";
                s.Message = "Cancelled.";
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI generate queue failed");
            SetSnapshot(s =>
            {
                s.LastError = ex.Message;
                s.Phase = "error";
                s.Message = ex.Message;
            });
        }
        finally
        {
            SetSnapshot(s =>
            {
                s.IsRunning = false;
                s.CompletedAt = DateTime.UtcNow;
                if (s.Phase is "starting" or "lineup" or "playout" or "priming")
                {
                    s.Phase = "done";
                }
            });
            Interlocked.Exchange(ref WorkerActive, 0);
            lock (QueueLock)
            {
                Snapshot.ChannelIds = [];
            }

            bool more;
            lock (QueueLock)
            {
                more = Pending.Count > 0;
            }

            if (more)
            {
                ScheduleDebounce();
            }
        }
    }

    private void UpdateJob(Guid channelId, AiChannelBuildProgress progress)
    {
        var job = Jobs.GetOrAdd(channelId, _ => new JobState());
        lock (job)
        {
            job.IsRunning = true;
            job.Phase = progress.Phase;
            job.CurrentDay = progress.CurrentDay;
            job.TotalDays = progress.TotalDays;
            job.ChannelName = progress.ChannelName;
            if (progress.Preview is not null)
            {
                job.Preview = progress.Preview;
                job.Applied = true;
            }
        }

        SetSnapshot(s =>
        {
            s.Phase = progress.Phase;
            s.ChannelName = progress.ChannelName;
            s.CurrentDay = progress.CurrentDay;
            if (progress.TotalDays > 0)
            {
                s.TotalDays = progress.TotalDays;
            }

            s.Message = FormatProgressMessage(progress);
            s.CompletedAt = null;
        });
    }

    private static string FormatProgressMessage(AiChannelBuildProgress progress)
    {
        var name = string.IsNullOrWhiteSpace(progress.ChannelName) ? "channel" : progress.ChannelName;
        var phase = progress.Phase ?? "";
        if (phase.StartsWith("priming", StringComparison.OrdinalIgnoreCase))
        {
            return phase.Contains("page", StringComparison.OrdinalIgnoreCase)
                ? $"{name} · {phase}"
                : $"Priming catalog for {name}";
        }

        if (phase is "generating" or "lineup")
        {
            return $"Generating weekly lineup for {name}";
        }

        if (phase == "playout")
        {
            var total = progress.TotalDays > 0 ? progress.TotalDays : PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            return $"Playout day {Math.Max(1, progress.CurrentDay)}/{total} · {name}";
        }

        if (phase == "horizon-full")
        {
            return $"{name} already has a {progress.TotalDays}-day guide";
        }

        if (phase is "queued" or "queuing")
        {
            return $"Queued {name}";
        }

        return string.IsNullOrWhiteSpace(phase) ? name : $"{name} · {phase}";
    }

    private void UpdateJobResult(Guid channelId, AiAutoApplyChannelResult result)
    {
        var job = Jobs.GetOrAdd(channelId, _ => new JobState());
        lock (job)
        {
            if (result.Preview is not null)
            {
                job.Preview = result.Preview;
                job.Applied = true;
            }

            if (!result.Ok && !result.WasSkipped)
            {
                job.ApplyError = result.Error;
            }

            if (result.WasSkipped)
            {
                job.Error = result.Error;
            }

            job.ChannelName = result.ChannelName;
        }
    }

    private static void SetSnapshot(Action<QueueSnapshot> update)
    {
        lock (QueueLock)
        {
            update(Snapshot);
        }
    }

    private sealed class QueueSnapshot
    {
        public bool IsRunning { get; set; }

        public List<Guid> ChannelIds { get; set; } = [];

        public string? Phase { get; set; }

        public string? ChannelName { get; set; }

        public string? Message { get; set; }

        public string? LastError { get; set; }

        public int CurrentDay { get; set; }

        public int TotalDays { get; set; }

        public int CurrentChannelIndex { get; set; }

        public int Page { get; set; }

        public int TotalPages { get; set; }

        public bool WasCancelled { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? CompletedAt { get; set; }
    }

    private sealed class JobState
    {
        public bool IsRunning { get; set; }

        public AiLineupPreviewResult? Preview { get; set; }

        public bool Applied { get; set; }

        public string? ApplyError { get; set; }

        public string? Error { get; set; }

        public string? Phase { get; set; }

        public int CurrentDay { get; set; }

        public int TotalDays { get; set; }

        public string? ChannelName { get; set; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }
    }
}
