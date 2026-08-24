using System.Collections.Concurrent;
using FinTv.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Runs single-channel AI lineup generation in the background so admin HTTP requests return immediately.
/// </summary>
public sealed class AiChannelGenerateJobService
{
    private static readonly ConcurrentDictionary<Guid, JobState> Jobs = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiChannelGenerateJobService> _logger;

    public AiChannelGenerateJobService(
        IServiceScopeFactory scopeFactory,
        ILogger<AiChannelGenerateJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning(Guid channelId)
    {
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
        var job = Jobs.GetOrAdd(channelId, _ => new JobState());
        lock (job)
        {
            if (job.IsRunning)
            {
                return false;
            }

            job.IsRunning = true;
            job.Error = null;
            job.Preview = null;
            job.Applied = false;
            job.ApplyError = null;
            job.StartedAtUtc = DateTime.UtcNow;
            job.CompletedAtUtc = null;
        }

        _logger.LogInformation("Queueing AI lineup generation for channel {ChannelId}", channelId);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var generator = scope.ServiceProvider.GetRequiredService<AiLineupGeneratorService>();
                var playout = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();
                var preview = await generator.GenerateAsync(channelId, providerOverride, CancellationToken.None)
                    .ConfigureAwait(false);

                var applied = false;
                string? applyError = null;
                try
                {
                    using var gate = await ChannelApplyLocks.AcquireAsync(channelId, CancellationToken.None)
                        .ConfigureAwait(false);
                    await generator.ApplyAsync(
                            channelId,
                            preview.LineupSlots,
                            rebuildPlayout: true,
                            playout,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    applied = true;
                }
                catch (Exception applyEx)
                {
                    applyError = applyEx is InvalidOperationException invalid
                        ? invalid.Message
                        : $"Playout rebuild failed: {applyEx.Message}";
                    _logger.LogWarning(
                        applyEx,
                        "AI lineup generated for {Channel} but the Live TV guide was not rebuilt",
                        preview.ChannelName);
                }

                lock (job)
                {
                    job.Preview = preview;
                    job.Applied = applied;
                    job.ApplyError = applyError;
                    job.CompletedAtUtc = DateTime.UtcNow;
                }

                _logger.LogInformation(
                    "AI lineup generation finished for {Channel} (guide rebuilt={Applied})",
                    preview.ChannelName,
                    applied);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI lineup generation failed for channel {ChannelId}", channelId);
                lock (job)
                {
                    job.Error = ex is InvalidOperationException invalid
                        ? invalid.Message
                        : $"AI lineup generation failed: {ex.Message}";
                    job.CompletedAtUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                lock (job)
                {
                    job.IsRunning = false;
                }
            }
        });

        return true;
    }

    public object BuildStatus(Guid channelId)
    {
        if (!Jobs.TryGetValue(channelId, out var job))
        {
            return new
            {
                channelId,
                isRunning = false,
                preview = (AiLineupPreviewResult?)null,
                applied = false,
                applyError = (string?)null,
                error = (string?)null
            };
        }

        lock (job)
        {
            return new
            {
                channelId,
                isRunning = job.IsRunning,
                startedAt = job.StartedAtUtc,
                completedAt = job.CompletedAtUtc,
                preview = job.Preview,
                applied = job.Applied,
                applyError = job.ApplyError,
                error = job.Error
            };
        }
    }

    private sealed class JobState
    {
        public bool IsRunning { get; set; }

        public AiLineupPreviewResult? Preview { get; set; }

        public bool Applied { get; set; }

        public string? ApplyError { get; set; }

        public string? Error { get; set; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }
    }
}
