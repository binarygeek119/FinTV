using System.Text.Json;
using FinTv.Api;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Services;

public sealed class CatalogIngestService
{
    private readonly FinTvDbContext _db;
    private readonly CatalogTypedStore _typedCatalog;
    private readonly CatalogCleanupService _catalogCleanup;
    private readonly CatalogSyncProgress _progress;

    public CatalogIngestService(
        FinTvDbContext db,
        CatalogTypedStore typedCatalog,
        CatalogCleanupService catalogCleanup,
        CatalogSyncProgress progress)
    {
        _db = db;
        _typedCatalog = typedCatalog;
        _catalogCleanup = catalogCleanup;
        _progress = progress;
    }

    public async Task<int> UpsertAsync(
        IReadOnlyList<CatalogItemDto> items,
        Guid? connectionId,
        bool markMissing,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        var incomingIds = items.Select(i => i.Id).ToHashSet();
        var saved = 0;
        foreach (var chunk in items.Chunk(200))
        {
            var ids = chunk.Select(i => i.Id).ToList();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                _db.ChangeTracker.Clear();
                var existing = await _db.MediaItems
                    .Include(i => i.Chapters)
                    .Where(i => ids.Contains(i.Id))
                    .ToDictionaryAsync(i => i.Id, cancellationToken);

                foreach (var item in chunk)
                {
                    item.SourceConnectionId = connectionId ?? item.SourceConnectionId;
                    ApplyMediaItem(existing, item);
                }

                await _typedCatalog.UpsertAsync(chunk.ToArray(), replaceAll: false, cancellationToken);
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < 2)
                {
                    // Catalog cleanup or another sync removed a row after we loaded it.
                }
            }

            saved += chunk.Length;
            _progress.Saving(saved, items.Count);
        }

        if (markMissing && incomingIds.Count > 0)
        {
            await FinishMissingAsync(incomingIds, connectionId, cancellationToken);
        }

        return saved;
    }

    public async Task FinishMissingAsync(
        IReadOnlySet<Guid> incomingIds,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        if (incomingIds.Count == 0)
        {
            return;
        }

        _progress.Finishing(incomingIds.Count);
        await _catalogCleanup.MarkMissingExceptAsync(incomingIds, connectionId, cancellationToken);
    }

    private void ApplyMediaItem(IReadOnlyDictionary<Guid, MediaItem> existing, CatalogItemDto item)
    {
        if (!existing.TryGetValue(item.Id, out var row))
        {
            row = new MediaItem { Id = item.Id };
            _db.MediaItems.Add(row);
        }

        row.Name = item.Name ?? string.Empty;
        row.SortName = item.SortName;
        row.Overview = string.IsNullOrWhiteSpace(item.Overview) ? item.Plot : item.Overview;
        row.Kind = item.Kind;
        row.Path = string.IsNullOrWhiteSpace(item.Path) ? item.JellyfinPath : item.Path;
        row.ParentId = item.ParentId;
        row.SeriesId = item.SeriesId;
        row.SeriesName = item.SeriesName;
        row.ProductionYear = item.ProductionYear;
        row.PremiereDate = item.PremiereDate;
        row.OfficialRating = item.OfficialRating;
        row.CommunityRating = item.CommunityRating;
        row.CriticRating = item.CriticRating;
        row.RuntimeTicks = item.RuntimeTicks;
        row.Runtime = string.IsNullOrWhiteSpace(item.Runtime) ? FormatRuntime(item.RuntimeTicks) : item.Runtime;
        row.IndexNumber = item.IndexNumber;
        row.ParentIndexNumber = item.ParentIndexNumber;
        row.LibraryId = item.LibraryId;
        row.LibraryName = item.LibraryName;
        row.SourceConnectionId = item.SourceConnectionId;
        row.CollectionType = item.CollectionType;
        row.PrimaryImagePath = item.PrimaryImagePath;
        row.Album = item.Album;
        row.MediaType = item.MediaType;
        row.SeasonId = item.SeasonId;
        row.SeasonName = item.SeasonName;
        row.GenresJson = JsonSerializer.Serialize(item.Genres ?? []);
        row.TagsJson = JsonSerializer.Serialize(item.Tags ?? []);
        row.StudiosJson = JsonSerializer.Serialize(item.Studios ?? []);
        row.CollectionNamesJson = JsonSerializer.Serialize(item.CollectionNames ?? []);
        row.PeopleJson = JsonSerializer.Serialize(
            item.People is { Count: > 0 }
                ? item.People
                : (item.Stars ?? []).Select(name => new CatalogPersonDto { Name = name, Type = "Actor" }));
        row.ProviderIdsJson = JsonSerializer.Serialize(item.ProviderIds ?? new Dictionary<string, string>());
        row.ArtistsJson = JsonSerializer.Serialize(item.Artists ?? []);
        row.AlbumArtistsJson = JsonSerializer.Serialize(item.AlbumArtists ?? []);
        row.Width = item.Width;
        row.Height = item.Height;
        row.AspectRatio = VideoAspectFormat.Classify(item.AspectRatio, item.Width, item.Height);
        row.SyncedAt = DateTime.UtcNow;
        row.IsMissing = false;
        row.MissingSince = null;

        _db.MediaChapters.RemoveRange(row.Chapters);
        row.Chapters.Clear();
        if (item.Chapters is { Count: > 0 })
        {
            foreach (var chapter in item.Chapters)
            {
                row.Chapters.Add(new MediaChapter
                {
                    MediaItemId = row.Id,
                    StartPositionTicks = chapter.StartPositionTicks,
                    Name = chapter.Name
                });
            }
        }
    }

    public static string FormatRuntime(long? ticks)
    {
        if (ticks is null or <= 0)
        {
            return string.Empty;
        }

        var time = TimeSpan.FromTicks(ticks.Value);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        }

        if (time.TotalMinutes >= 1)
        {
            return $"{(int)time.TotalMinutes}m {time.Seconds:00}s";
        }

        return $"{time.Seconds}s";
    }
}
