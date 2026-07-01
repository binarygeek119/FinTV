using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class CommercialService
{
    private readonly FinTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinCatalogService _catalog;

    private readonly IChapterManager _chapterManager;

    public CommercialService(FinTvDbContext db, ILibraryManager libraryManager, JellyfinCatalogService catalog, IChapterManager chapterManager)
    {
        _db = db;
        _libraryManager = libraryManager;
        _catalog = catalog;
        _chapterManager = chapterManager;
    }

    public async Task InsertCommercialsAsync(
        Channel channel,
        ResolvedCandidate content,
        DateTime contentStart,
        DateTime contentEnd,
        CancellationToken cancellationToken)
    {
        if (channel.CommercialPresetId is null || content.JellyfinItemId is null)
        {
            return;
        }

        var preset = await _db.CommercialPresets
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == channel.CommercialPresetId, cancellationToken);

        if (preset is null)
        {
            return;
        }

        var item = _libraryManager.GetItemById(content.JellyfinItemId.Value);
        if (item is null)
        {
            return;
        }

        var breakPoints = GetBreakPoints(item, preset, contentStart, contentEnd);
        foreach (var point in breakPoints)
        {
            var commercials = await PickCommercialsAsync(preset.PostRollCount > 0 ? preset.PostRollCount : 1, cancellationToken);
            var cursor = point;
            foreach (var commercial in commercials)
            {
                var end = cursor.Add(commercial.Duration);
                if (end > contentEnd)
                {
                    break;
                }

                _db.PlayoutItems.Add(new PlayoutItem
                {
                    ChannelId = channel.Id,
                    JellyfinItemId = commercial.JellyfinItemId,
                    Start = cursor,
                    Finish = end,
                    Title = commercial.Title,
                    FillerKind = FillerKind.PostRoll,
                    GuideGroup = "commercial"
                });

                cursor = end;
            }
        }
    }

    public async Task<List<Commercial>> PickCommercialsAsync(int count, CancellationToken cancellationToken)
    {
        var all = await _db.Commercials.AsNoTracking().ToListAsync(cancellationToken);
        if (all.Count == 0)
        {
            return new List<Commercial>();
        }

        var rng = Random.Shared;
        return Enumerable.Range(0, count)
            .Select(_ => all[rng.Next(all.Count)])
            .ToList();
    }

    public async Task SyncCommercialLibraryAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        var tag = config?.CommercialLibraryTag ?? "fintv-commercial";
        var query = new InternalItemsQuery
        {
            Recursive = true,
            Tags = new[] { tag }
        };

        var items = _libraryManager.GetItemsResult(query).Items;
        foreach (var item in items)
        {
            var existing = await _db.Commercials.FirstOrDefaultAsync(c => c.JellyfinItemId == item.Id, cancellationToken);
            var duration = _catalog.GetRuntime(item);
            if (existing is null)
            {
                _db.Commercials.Add(new Commercial
                {
                    JellyfinItemId = item.Id,
                    Title = item.Name,
                    Duration = duration
                });
            }
            else
            {
                existing.Title = item.Name;
                existing.Duration = duration;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<Commercial>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Commercials
            .Include(c => c.Chapters)
            .AsNoTracking()
            .OrderBy(c => c.Title)
            .ToListAsync(cancellationToken);
    }

    private List<DateTime> GetBreakPoints(BaseItem item, CommercialPreset preset, DateTime contentStart, DateTime contentEnd)
    {
        var points = new List<DateTime>();

        if (preset.BreakMode is CommercialBreakMode.ChaptersThenTimer or CommercialBreakMode.ChaptersOnly)
        {
            var chapters = _chapterManager.GetChapters(item.Id);
            foreach (var chapter in chapters.Where(c => !string.IsNullOrWhiteSpace(c.Name)
                && c.Name.Contains("commercial", StringComparison.OrdinalIgnoreCase)))
            {
                var chapterTime = contentStart.AddTicks(chapter.StartPositionTicks);
                if (chapterTime > contentStart && chapterTime < contentEnd)
                {
                    points.Add(chapterTime);
                }
            }
        }

        if (points.Count == 0 && preset.BreakMode is CommercialBreakMode.ChaptersThenTimer or CommercialBreakMode.TimerOnly)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, preset.TimerIntervalMinutes));
            var cursor = contentStart.Add(interval);
            while (cursor < contentEnd)
            {
                points.Add(cursor);
                cursor = cursor.Add(interval);
            }
        }

        if (preset.PreRollCount > 0)
        {
            points.Insert(0, contentStart);
        }

        return points.Distinct().OrderBy(p => p).ToList();
    }
}
