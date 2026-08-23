using System.Text.Json;
using System.Text.RegularExpressions;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed record NewsBulletinRunResult(
    bool Created,
    bool Skipped,
    string? Path,
    int NewStoryCount,
    int EncodedStoryCount,
    string? SkipReason,
    DateTimeOffset RanAt);

public sealed class NewsBulletinService
{
    public const int IntervalHours = 6;
    private const int MaxKeptVideos = 12;
    private const int MaxTrackedStories = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly NewsHeadlineService _headlines;
    private readonly ILogger<NewsBulletinService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NewsBulletinService(
        IServiceScopeFactory scopes,
        NewsHeadlineService headlines,
        ILogger<NewsBulletinService> logger)
    {
        _scopes = scopes;
        _headlines = headlines;
        _logger = logger;
    }

    public static DateTimeOffset NextSixHourMark(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var nextHour = ((local.Hour / IntervalHours) + 1) * IntervalHours;
        var next = nextHour >= 24
            ? local.Date.AddDays(1)
            : local.Date.AddHours(nextHour);
        return new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), local.Offset);
    }

    public object DescribeStatus(NewsSettings? settings = null)
    {
        var ledger = LoadLedger();
        var enabled = settings?.BulletinVideosEnabled ?? true;
        var min = ClampMin(settings?.MinNewStories ?? 1);
        return new
        {
            enabled,
            intervalHours = IntervalHours,
            minNewStories = min,
            nextRunAt = NextSixHourMark(DateTimeOffset.Now),
            lastRunAt = ledger.LastRunAt,
            lastCreated = ledger.LastCreated,
            lastSkipReason = ledger.LastSkipReason,
            lastVideoPath = ledger.LastVideoPath,
            lastNewStoryCount = ledger.LastNewStoryCount,
            lastEncodedStoryCount = ledger.LastEncodedStoryCount
        };
    }

    public async Task<NewsBulletinRunResult> RunAsync(bool scheduled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RunCoreAsync(scheduled, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NewsBulletinRunResult> RunCoreAsync(bool scheduled, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var renderer = scope.ServiceProvider.GetRequiredService<NewsChannelService>();
        var settings = await db.NewsSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? new NewsSettings();
        var ledger = LoadLedger();
        var ranAt = DateTimeOffset.Now;
        var min = ClampMin(settings.MinNewStories);

        if (scheduled && !settings.BulletinVideosEnabled)
        {
            return SaveSkip(ledger, ranAt, "Bulletin videos are turned off.", 0);
        }

        var articles = await _headlines.GetAsync(force: true, cancellationToken);
        var used = new HashSet<string>(ledger.Keys, StringComparer.Ordinal);
        var newStories = articles.Where(article => !used.Contains(StoryKey(article))).ToList();

        if (newStories.Count == 0)
        {
            return SaveSkip(ledger, ranAt, "No new stories.", 0);
        }

        if (newStories.Count < min)
        {
            return SaveSkip(
                ledger,
                ranAt,
                $"Only {newStories.Count} new {(newStories.Count == 1 ? "story" : "stories")} (minimum {min}).",
                newStories.Count);
        }

        var encoded = newStories.Take(Math.Clamp(settings.ArticleCount, 1, 30)).ToList();
        var newsRoot = FinTvRuntime.Current.NewsFolder;
        var stamp = ranAt.ToLocalTime().ToString("yyyyMMdd-HHmm");
        var workDir = Path.Combine(newsRoot, "bulletins", "work-" + stamp);
        var outputMp4 = Path.Combine(newsRoot, "bulletins", $"news-{stamp}.mp4");
        var header = string.IsNullOrWhiteSpace(settings.HeaderText) ? "FlowWire News" : settings.HeaderText.Trim();

        var ok = await renderer.RenderBulletinFileAsync(
            settings,
            encoded,
            header,
            workDir,
            outputMp4,
            cancellationToken);

        TryDeleteDirectory(workDir);

        if (!ok)
        {
            return SaveSkip(ledger, ranAt, "FFmpeg failed to create the news video.", encoded.Count);
        }

        foreach (var story in encoded)
        {
            ledger.Keys.Add(StoryKey(story));
        }

        if (ledger.Keys.Count > MaxTrackedStories)
        {
            ledger.Keys = ledger.Keys.Skip(ledger.Keys.Count - MaxTrackedStories).ToList();
        }

        ledger.LastRunAt = ranAt;
        ledger.LastCreated = true;
        ledger.LastSkipReason = null;
        ledger.LastVideoPath = outputMp4;
        ledger.LastNewStoryCount = newStories.Count;
        ledger.LastEncodedStoryCount = encoded.Count;
        SaveLedger(ledger);
        PruneOldVideos(Path.Combine(newsRoot, "bulletins"));

        _logger.LogInformation(
            "News bulletin created {Path} with {Encoded} of {New} new stories",
            outputMp4,
            encoded.Count,
            newStories.Count);

        return new NewsBulletinRunResult(true, false, outputMp4, newStories.Count, encoded.Count, null, ranAt);
    }

    private NewsBulletinRunResult SaveSkip(NewsStoryLedger ledger, DateTimeOffset ranAt, string reason, int newCount)
    {
        ledger.LastRunAt = ranAt;
        ledger.LastCreated = false;
        ledger.LastSkipReason = reason;
        ledger.LastNewStoryCount = newCount;
        ledger.LastEncodedStoryCount = 0;
        SaveLedger(ledger);
        _logger.LogInformation("News bulletin skipped: {Reason}", reason);
        return new NewsBulletinRunResult(false, true, ledger.LastVideoPath, newCount, 0, reason, ranAt);
    }

    internal static int ClampMin(int value) => Math.Clamp(value <= 0 ? 1 : value, 1, 30);

    internal static string StoryKey(NewsArticle article)
    {
        var title = Regex.Replace((article.Title ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        return title.Length > 240 ? title[..240] : title;
    }

    private static string LedgerPath()
        => Path.Combine(FinTvRuntime.Current.NewsFolder, "story-ledger.json");

    private NewsStoryLedger LoadLedger()
    {
        try
        {
            var path = LedgerPath();
            if (!File.Exists(path))
            {
                return new NewsStoryLedger();
            }

            var parsed = JsonSerializer.Deserialize<NewsStoryLedger>(File.ReadAllText(path), JsonOptions);
            return parsed ?? new NewsStoryLedger();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read news story ledger");
            return new NewsStoryLedger();
        }
    }

    private void SaveLedger(NewsStoryLedger ledger)
    {
        var path = LedgerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(ledger, JsonOptions));
    }

    private static void PruneOldVideos(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var files = Directory.GetFiles(folder, "news-*.mp4")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Skip(MaxKeptVideos)
            .ToList();
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // leave extras if they are in use
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // work files are under config/news and will be overwritten next run
        }
    }

    private sealed class NewsStoryLedger
    {
        public List<string> Keys { get; set; } = [];

        public DateTimeOffset? LastRunAt { get; set; }

        public bool LastCreated { get; set; }

        public string? LastSkipReason { get; set; }

        public string? LastVideoPath { get; set; }

        public int LastNewStoryCount { get; set; }

        public int LastEncodedStoryCount { get; set; }
    }
}
