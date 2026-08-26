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
            job.Phase = "generating";
            job.CurrentDay = 1;
            job.TotalDays = PlayoutScheduleHelper.GetPlayoutDaysToBuild();
            job.ChannelName = null;
            job.StartedAtUtc = DateTime.UtcNow;
            job.CompletedAtUtc = null;
        }

        _logger.LogInformation("Queueing AI lineup generation for channel {ChannelId}", channelId);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var autoApply = scope.ServiceProvider.GetRequiredService<AiChannelAutoApplyService>();
                var result = await autoApply.GenerateAndBuildHorizonDaysAsync(
                        channelId,
                        providerOverride,
                        onProgress: progress =>
                        {
                            lock (job)
                            {
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
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                lock (job)
                {
                    if (result.Preview is not null)
                    {
                        job.Preview = result.Preview;
                    }

                    if (result.Ok)
                    {
                        job.Applied = true;
                        job.Phase = result.PlayoutAlreadyAtHorizon ? "horizon-full" : "done";
                    }
                    else if (result.WasSkipped)
                    {
                        job.Error = result.Error;
                    }
                    else
                    {
                        job.Applied = result.Preview is not null;
                        job.ApplyError = result.Error;
                    }

                    job.CompletedAtUtc = DateTime.UtcNow;
                }

                _logger.LogInformation(
                    "AI lineup generation finished for {Channel} (ok={Ok})",
                    result.ChannelName ?? channelId.ToString(),
                    result.Ok);
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
        var rebuild = _playoutBuilder.GetRebuildState(channelId);
        if (!Jobs.TryGetValue(channelId, out var job))
        {
            return new
            {
                channelId,
                isRunning = false,
                preview = (AiLineupPreviewResult?)null,
                applied = false,
                applyError = (string?)null,
                error = (string?)null,
                phase = (string?)null,
                currentDay = 0,
                totalDays = PlayoutScheduleHelper.GetPlayoutDaysToBuild(),
                channelName = (string?)null,
                rebuild
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
                error = job.Error,
                phase = job.Phase,
                currentDay = job.CurrentDay,
                totalDays = job.TotalDays,
                channelName = job.ChannelName,
                rebuild
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

        public string? Phase { get; set; }

        public int CurrentDay { get; set; }

        public int TotalDays { get; set; }

        public string? ChannelName { get; set; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }
    }
}
