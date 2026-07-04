using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Jellyfin library search helpers for the FinTV admin UI.
/// </summary>
[ApiController]
[Route("FinTV/api/catalog")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CatalogController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly JellyfinCatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="catalog">FinTV catalog service.</param>
    public CatalogController(ILibraryManager libraryManager, JellyfinCatalogService catalog)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
    }

    /// <summary>
    /// Searches Jellyfin library items for lineup slot assignment.
    /// </summary>
    /// <param name="q">Search text.</param>
    /// <param name="contentType">Optional channel content type filter.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library items.</returns>
    [HttpGet("search")]
    public ActionResult<IEnumerable<object>> Search(
        [FromQuery] string q,
        [FromQuery] ChannelContentType? contentType,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return Ok(Array.Empty<object>());
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            SearchTerm = q.Trim(),
            Limit = Math.Clamp(limit, 1, 50),
            IncludeItemTypes = contentType.HasValue
                ? GetItemTypes(contentType.Value)
                : new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Audio },
            OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
        };

        var items = _libraryManager.GetItemsResult(query).Items;
        return Ok(items.Select(MapSearchResult));
    }

    /// <summary>
    /// Resolves display metadata for Jellyfin item identifiers.
    /// </summary>
    /// <param name="request">Lookup request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved item metadata.</returns>
    [HttpPost("lookup")]
    public ActionResult<IEnumerable<object>> Lookup([FromBody] CatalogLookupRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Ids is not { Count: > 0 })
        {
            return Ok(Array.Empty<object>());
        }

        var results = new List<object>();
        foreach (var id in request.Ids.Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null)
            {
                results.Add(MapSearchResult(item));
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Browses library items by tag for AI lineup generation.
    /// </summary>
    /// <param name="tag">Library tag filter.</param>
    /// <param name="contentType">Optional channel content type.</param>
    /// <param name="catalogMode">Optional catalog mode override.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library items.</returns>
    [HttpGet("browse")]
    public ActionResult<object> Browse(
        [FromQuery] string? tag,
        [FromQuery] ChannelContentType? contentType,
        [FromQuery] ChannelCatalogMode? catalogMode,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = new Channel
        {
            ContentType = contentType ?? ChannelContentType.TvShow,
            FilterJson = string.IsNullOrWhiteSpace(tag)
                ? null
                : FinTvJson.Serialize(new { tags = new[] { tag } }),
            CatalogMode = catalogMode
        };

        var mode = JellyfinCatalogService.ResolveCatalogMode(channel);
        var items = _catalog.BrowseForAiManifest(channel, mode, Math.Clamp(limit, 1, 500));
        return Ok(new
        {
            catalogMode = mode.ToString(),
            total = items.Count,
            items = items.Select(MapSearchResult)
        });
    }

    private static object MapSearchResult(BaseItem item)
    {
        var runtime = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : (TimeSpan?)null;

        return new
        {
            id = item.Id,
            name = item.Name,
            type = item.GetBaseItemKind().ToString(),
            runtimeMinutes = runtime.HasValue ? (int)Math.Round(runtime.Value.TotalMinutes) : (int?)null,
            year = item.ProductionYear
        };
    }

    private static BaseItemKind[] GetItemTypes(ChannelContentType contentType)
    {
        return contentType switch
        {
            ChannelContentType.TvShow => new[] { BaseItemKind.Episode },
            ChannelContentType.Movie => new[] { BaseItemKind.Movie },
            ChannelContentType.MusicVideo => new[] { BaseItemKind.MusicVideo },
            ChannelContentType.Music => new[] { BaseItemKind.Audio },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Audio }
        };
    }
}

/// <summary>
/// Request body for catalog item lookup.
/// </summary>
public class CatalogLookupRequest
{
    /// <summary>
    /// Gets or sets Jellyfin item identifiers to resolve.
    /// </summary>
    public List<Guid> Ids { get; set; } = new();
}
