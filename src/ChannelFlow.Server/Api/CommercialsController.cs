using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// REST endpoints for commercial library management and blackframe scanning.
/// </summary>
[ApiController]
[Route("api/commercials")]
[Authorize(Policy = "admin")]
public class CommercialsController : ControllerBase
{
    private readonly CommercialService _commercials;
    private readonly CommercialBrainzSyncService _commercialBrainz;
    private readonly BlackframeChapterTask _blackframeTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommercialsController"/> class.
    /// </summary>
    /// <param name="commercials">Commercial service.</param>
    /// <param name="commercialBrainz">CommercialBrainz sync service.</param>
    /// <param name="blackframeTask">Blackframe detection task.</param>
    public CommercialsController(
        CommercialService commercials,
        CommercialBrainzSyncService commercialBrainz,
        BlackframeChapterTask blackframeTask)
    {
        _commercials = commercials;
        _commercialBrainz = commercialBrainz;
        _blackframeTask = blackframeTask;
    }

    /// <summary>
    /// Gets all commercials in the ChannelFlow library.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of commercials.</returns>
    [HttpGet]
    public async Task<ActionResult<List<Commercial>>> GetAll(CancellationToken cancellationToken)
    {
        return await _commercials.GetAllAsync(cancellationToken);
    }

    /// <summary>
    /// Gets CommercialBrainz settings for the admin UI.
    /// </summary>
    /// <returns>CommercialBrainz settings.</returns>
    [HttpGet("brainz/settings")]
    public ActionResult<object> GetBrainzSettings()
    {
        var settings = FinTvRuntime.Current?.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
        return Ok(MapBrainzSettings(settings));
    }

    /// <summary>
    /// Updates CommercialBrainz settings.
    /// </summary>
    /// <param name="request">Settings payload.</param>
    /// <returns>Updated settings.</returns>
    [HttpPut("brainz/settings")]
    public ActionResult<object> UpdateBrainzSettings([FromBody] CommercialBrainzSettingsRequest request)
    {
        var plugin = FinTvRuntime.Current;
        if (plugin is null)
        {
            return NotFound();
        }

        var settings = plugin.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
        settings.Enabled = request.Enabled;
        settings.BaseUrl = CommercialBrainzSettings.NormalizeBaseUrl(request.BaseUrl);
        settings.ApiToken = string.IsNullOrWhiteSpace(request.ApiToken)
            ? settings.ApiToken
            : request.ApiToken.Trim();
        settings.PoolMode = request.PoolMode;
        settings.MaxSyncResults = Math.Clamp(request.MaxSyncResults, 1, 5000);
        settings.MinYear = request.MinYear;
        settings.MaxYear = request.MaxYear;
        settings.Decades = request.Decades?
            .Where(decade => decade >= 1900)
            .Distinct()
            .ToList()
            ?? new List<int>();
        settings.Brands = NormalizeList(request.Brands);
        settings.Tags = NormalizeList(request.Tags);
        settings.ExcludeTags = NormalizeList(request.ExcludeTags);
        settings.Genres = NormalizeList(request.Genres);
        settings.Networks = NormalizeList(request.Networks);
        settings.ChannelNames = NormalizeList(request.ChannelNames);
        settings.MinAgeLimit = request.MinAgeLimit;
        settings.MaxAgeLimit = request.MaxAgeLimit;
        settings.AllowSpoof = request.AllowSpoof;
        settings.AllowFake = request.AllowFake;
        settings.AllowReal = request.AllowReal;
        settings.AllowAiEnhanced = request.AllowAiEnhanced;
        settings.AllowLateNight = request.AllowLateNight;
        settings.AllowAdultRated = request.AllowAdultRated;
        settings.AllowBanned = request.AllowBanned;

        plugin.Configuration.CommercialBrainz = settings;
        plugin.SaveConfiguration();
        return Ok(MapBrainzSettings(settings));
    }

    /// <summary>
    /// Gets CommercialBrainz sync status.
    /// </summary>
    /// <returns>Sync status.</returns>
    [HttpGet("brainz/status")]
    public ActionResult<object> GetBrainzStatus()
    {
        var settings = FinTvRuntime.Current?.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
        return Ok(settings.SyncState);
    }

    /// <summary>
    /// Previews CommercialBrainz matches without writing to the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preview counts and sample matches.</returns>
    [HttpPost("brainz/preview")]
    public async Task<ActionResult<CommercialBrainzPreviewResult>> PreviewBrainz(CancellationToken cancellationToken)
    {
        return await _commercialBrainz.PreviewAsync(cancellationToken);
    }

    /// <summary>
    /// Proxies a YouTube thumbnail so the ChannelFlow dashboard can display preview cards.
    /// </summary>
    /// <param name="youtubeId">YouTube video id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JPEG thumbnail bytes.</returns>
    [HttpGet("brainz/thumbnail/{youtubeId}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> GetBrainzThumbnail(string youtubeId, CancellationToken cancellationToken)
    {
        var bytes = await _commercialBrainz.GetYouTubeThumbnailAsync(youtubeId, cancellationToken);
        if (bytes is null || bytes.Length == 0)
        {
            return NotFound();
        }

        return File(bytes, "image/jpeg");
    }

    /// <summary>
    /// Syncs commercials from CommercialBrainz using the configured filters.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted when sync starts.</returns>
    [HttpPost("brainz/sync")]
    public async Task<IActionResult> SyncBrainz(CancellationToken cancellationToken)
    {
        await _commercialBrainz.SyncAsync(cancellationToken);
        return Accepted(new { FinTvRuntime.Current?.Configuration.CommercialBrainz?.SyncState });
    }

    /// <summary>
    /// Syncs commercials from the configured Jellyfin library tag.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted when sync starts.</returns>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        await _commercials.SyncCommercialLibraryAsync(cancellationToken);
        return Accepted();
    }

    /// <summary>
    /// Runs FFmpeg blackframe detection on all commercial items.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted with current task state.</returns>
    [HttpPost("scan-blackframes")]
    public async Task<IActionResult> ScanBlackframes(CancellationToken cancellationToken)
    {
        await _blackframeTask.ExecuteAsync(new Progress<double>(), cancellationToken);
        return Accepted(new { FinTvRuntime.Current?.Configuration.BlackframeTaskState });
    }

    /// <summary>
    /// Gets the current blackframe scan task status.
    /// </summary>
    /// <returns>Task progress state.</returns>
    [HttpGet("scan-status")]
    public ActionResult<object> ScanStatus()
    {
        return Ok(FinTvRuntime.Current?.Configuration.BlackframeTaskState);
    }

    [HttpGet("search-playlists")]
    public async Task<ActionResult<object>> GetSearchPlaylists(CancellationToken cancellationToken)
    {
        var playlists = FinTvRuntime.Current?.Configuration.CommercialSearchPlaylists ?? new List<CommercialSearchPlaylist>();
        var mapped = new List<object>();
        foreach (var playlist in playlists.OrderBy(p => p.Name))
        {
            mapped.Add(await _commercialBrainz.MapSearchPlaylistAsync(playlist, cancellationToken));
        }

        return Ok(mapped);
    }

    [HttpPost("search-playlists")]
    public async Task<ActionResult<object>> CreateSearchPlaylist(
        [FromBody] CommercialSearchPlaylistRequest? request,
        CancellationToken cancellationToken)
    {
        var runtime = FinTvRuntime.Current;
        if (runtime is null)
        {
            return NotFound();
        }

        var name = request?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Playlist name is required." });
        }

        var playlist = new CommercialSearchPlaylist { Name = name };
        ApplyPlaylistFilters(playlist, request);
        runtime.Configuration.CommercialSearchPlaylists.Add(playlist);
        runtime.SaveConfiguration();

        return Ok(await PullPlaylistIntoLibraryAsync(playlist.Id, cancellationToken));
    }

    [HttpPut("search-playlists/{id:guid}")]
    public async Task<ActionResult<object>> UpdateSearchPlaylist(
        Guid id,
        [FromBody] CommercialSearchPlaylistRequest? request,
        CancellationToken cancellationToken)
    {
        var runtime = FinTvRuntime.Current;
        if (runtime is null)
        {
            return NotFound();
        }

        var playlist = runtime.Configuration.CommercialSearchPlaylists.FirstOrDefault(p => p.Id == id);
        if (playlist is null)
        {
            return NotFound(new { message = "Search playlist not found." });
        }

        var name = request?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
        {
            playlist.Name = name;
        }

        ApplyPlaylistFilters(playlist, request);
        runtime.SaveConfiguration();
        return Ok(await PullPlaylistIntoLibraryAsync(playlist.Id, cancellationToken));
    }

    [HttpPost("search-playlists/{id:guid}/pull")]
    public async Task<ActionResult<object>> PullSearchPlaylist(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var pulled = await _commercialBrainz.PullSearchPlaylistAsync(id, cancellationToken);
            return Ok(await _commercialBrainz.MapSearchPlaylistAsync(pulled, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("search-playlists/{id:guid}")]
    public IActionResult DeleteSearchPlaylist(Guid id)
    {
        var runtime = FinTvRuntime.Current;
        if (runtime is null)
        {
            return NotFound();
        }

        var removed = runtime.Configuration.CommercialSearchPlaylists.RemoveAll(p => p.Id == id);
        if (removed == 0)
        {
            return NotFound(new { message = "Search playlist not found." });
        }

        runtime.SaveConfiguration();
        return NoContent();
    }

    private async Task<object> PullPlaylistIntoLibraryAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        try
        {
            var pulled = await _commercialBrainz.PullSearchPlaylistAsync(playlistId, cancellationToken);
            return await _commercialBrainz.MapSearchPlaylistAsync(pulled, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var playlist = FinTvRuntime.Current?.Configuration.CommercialSearchPlaylists
                .FirstOrDefault(p => p.Id == playlistId);
            if (playlist is null)
            {
                throw;
            }

            return await _commercialBrainz.MapSearchPlaylistAsync(playlist, cancellationToken);
        }
    }

    private static object MapBrainzSettings(CommercialBrainzSettings settings)
    {
        return new
        {
            enabled = settings.Enabled,
            baseUrl = CommercialBrainzSettings.NormalizeBaseUrl(settings.BaseUrl),
            hasApiToken = !string.IsNullOrWhiteSpace(settings.ApiToken),
            poolMode = (int)settings.PoolMode,
            maxSyncResults = settings.MaxSyncResults,
            minYear = settings.MinYear,
            maxYear = settings.MaxYear,
            decades = settings.Decades,
            brands = settings.Brands,
            tags = settings.Tags,
            excludeTags = settings.ExcludeTags,
            genres = settings.Genres,
            networks = settings.Networks,
            channelNames = settings.ChannelNames,
            minAgeLimit = settings.MinAgeLimit,
            maxAgeLimit = settings.MaxAgeLimit,
            allowSpoof = settings.AllowSpoof,
            allowFake = settings.AllowFake,
            allowReal = settings.AllowReal,
            allowAiEnhanced = settings.AllowAiEnhanced,
            allowLateNight = settings.AllowLateNight,
            allowAdultRated = settings.AllowAdultRated,
            allowBanned = settings.AllowBanned,
            syncState = settings.SyncState
        };
    }

    private static void ApplyPlaylistFilters(CommercialSearchPlaylist playlist, CommercialSearchPlaylistRequest? request)
    {
        playlist.Query = request?.Query?.Trim() ?? string.Empty;
        playlist.MaxResults = Math.Clamp(request?.MaxResults ?? playlist.MaxResults, 1, 500);
        playlist.MinYear = request?.MinYear;
        playlist.MaxYear = request?.MaxYear;
        playlist.Decades = request?.Decades?
            .Where(decade => decade >= 1900)
            .Distinct()
            .ToList()
            ?? new List<int>();
        playlist.Brands = NormalizeList(request?.Brands);
        playlist.Tags = NormalizeList(request?.Tags);
        playlist.ExcludeTags = NormalizeList(request?.ExcludeTags);
        playlist.Genres = NormalizeList(request?.Genres);
        playlist.Networks = NormalizeList(request?.Networks);
        playlist.ChannelNames = NormalizeList(request?.ChannelNames);
        playlist.MinAgeLimit = request?.MinAgeLimit;
        playlist.MaxAgeLimit = request?.MaxAgeLimit;
        playlist.AllowSpoof = request?.AllowSpoof ?? true;
        playlist.AllowFake = request?.AllowFake ?? true;
        playlist.AllowReal = request?.AllowReal ?? true;
        playlist.AllowAiEnhanced = request?.AllowAiEnhanced ?? true;
        playlist.AllowLateNight = request?.AllowLateNight ?? true;
        playlist.AllowAdultRated = request?.AllowAdultRated ?? false;
        playlist.AllowBanned = request?.AllowBanned ?? false;
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }
}

public class CommercialBrainzSettingsRequest
{
    public bool Enabled { get; set; } = true;

    public string? BaseUrl { get; set; }

    public string? ApiToken { get; set; }

    public CommercialPoolMode PoolMode { get; set; } = CommercialPoolMode.Both;

    public int MaxSyncResults { get; set; } = 500;

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    public List<int>? Decades { get; set; }

    public List<string>? Brands { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? ExcludeTags { get; set; }

    public List<string>? Genres { get; set; }

    public List<string>? Networks { get; set; }

    public List<string>? ChannelNames { get; set; }

    public int? MinAgeLimit { get; set; }

    public int? MaxAgeLimit { get; set; }

    public bool AllowSpoof { get; set; } = true;

    public bool AllowFake { get; set; } = true;

    public bool AllowReal { get; set; } = true;

    public bool AllowAiEnhanced { get; set; } = true;

    public bool AllowLateNight { get; set; } = true;

    public bool AllowAdultRated { get; set; }

    public bool AllowBanned { get; set; }
}

public class CommercialSearchPlaylistRequest
{
    public string? Name { get; set; }

    public string? Query { get; set; }

    public int? MaxResults { get; set; }

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    public List<int>? Decades { get; set; }

    public List<string>? Brands { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? ExcludeTags { get; set; }

    public List<string>? Genres { get; set; }

    public List<string>? Networks { get; set; }

    public List<string>? ChannelNames { get; set; }

    public int? MinAgeLimit { get; set; }

    public int? MaxAgeLimit { get; set; }

    public bool? AllowSpoof { get; set; }

    public bool? AllowFake { get; set; }

    public bool? AllowReal { get; set; }

    public bool? AllowAiEnhanced { get; set; }

    public bool? AllowLateNight { get; set; }

    public bool? AllowAdultRated { get; set; }

    public bool? AllowBanned { get; set; }
}
