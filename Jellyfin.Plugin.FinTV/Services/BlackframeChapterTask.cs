using System.Text.RegularExpressions;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public partial class BlackframeChapterTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BlackframeChapterTask> _logger;

    public BlackframeChapterTask(IServiceScopeFactory scopeFactory, ILogger<BlackframeChapterTask> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string Name => "FinTV Commercial Blackframe Detection";

    public string Key => "FinTVBlackframe";

    public string Description => "Detect commercial segments using FFmpeg blackframe analysis and store chapter markers.";

    public string Category => "FinTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var commercialService = scope.ServiceProvider.GetRequiredService<CommercialService>();
        var ffmpegBuilder = scope.ServiceProvider.GetRequiredService<FfmpegCommandBuilder>();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var catalog = scope.ServiceProvider.GetRequiredService<JellyfinCatalogService>();
        var mediaEncoder = scope.ServiceProvider.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>();

        var state = Plugin.Instance?.Configuration.BlackframeTaskState ?? new BlackframeTaskState();
        state.IsRunning = true;
        state.LastError = null;
        SaveState(state);

        await commercialService.SyncCommercialLibraryAsync(cancellationToken);
        var commercials = await db.Commercials.Include(c => c.Chapters).ToListAsync(cancellationToken);
        state.TotalItems = commercials.Count;
        state.ProcessedItems = 0;
        SaveState(state);

        var ffmpegPath = mediaEncoder.EncoderPath;
        var index = 0;

        foreach (var commercial in commercials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (commercial.Source != CommercialSource.Jellyfin || commercial.JellyfinItemId == Guid.Empty)
            {
                continue;
            }

            var item = libraryManager.GetItemById(commercial.JellyfinItemId);
            if (item is null)
            {
                continue;
            }

            var path = catalog.GetMediaPath(item);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var stderrBuilder = new System.Text.StringBuilder();
                await CliWrap.Cli.Wrap(ffmpegPath)
                    .WithArguments(ffmpegBuilder.BuildBlackdetectCommand(path))
                    .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(stderrBuilder))
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);

                var chapters = ParseBlackframes(stderrBuilder.ToString());
                db.CommercialChapters.RemoveRange(commercial.Chapters);
                commercial.Chapters.Clear();

                var i = 0;
                foreach (var chapter in chapters)
                {
                    commercial.Chapters.Add(new CommercialChapter
                    {
                        CommercialId = commercial.Id,
                        Start = chapter.Start,
                        End = chapter.End,
                        Name = $"Commercial {++i}",
                        DetectedByBlackframe = true
                    });
                }

                commercial.LastScannedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                _logger.LogError(ex, "Blackframe scan failed for {Title}", commercial.Title);
            }

            index++;
            state.ProcessedItems = index;
            SaveState(state);
            progress.Report(commercials.Count == 0 ? 100 : index * 100d / commercials.Count);
        }

        state.IsRunning = false;
        state.LastCompletedAt = DateTime.UtcNow;
        SaveState(state);
    }

    private static void SaveState(BlackframeTaskState state)
    {
        if (Plugin.Instance is null)
        {
            return;
        }

        Plugin.Instance.Configuration.BlackframeTaskState = state;
        Plugin.Instance.SaveConfiguration();
    }

    public static List<(TimeSpan Start, TimeSpan End)> ParseBlackframes(string stderr)
    {
        var results = new List<(TimeSpan Start, TimeSpan End)>();
        TimeSpan? start = null;

        foreach (Match match in BlackStartRegex().Matches(stderr))
        {
            if (double.TryParse(match.Groups[1].Value, out var seconds))
            {
                start = TimeSpan.FromSeconds(seconds);
            }
        }

        foreach (Match match in BlackEndRegex().Matches(stderr))
        {
            if (start.HasValue && double.TryParse(match.Groups[1].Value, out var seconds))
            {
                results.Add((start.Value, TimeSpan.FromSeconds(seconds)));
                start = null;
            }
        }

        return results;
    }

    [GeneratedRegex(@"black_start:(\d+(?:\.\d+)?)")]
    private static partial Regex BlackStartRegex();

    [GeneratedRegex(@"black_end:(\d+(?:\.\d+)?)")]
    private static partial Regex BlackEndRegex();
}
