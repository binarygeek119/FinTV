using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// REST endpoints for commercial library management and blackframe scanning.
/// </summary>
[ApiController]
[Route("FinTV/api/commercials")]
[Authorize(Policy = Policies.RequiresElevation)]
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
    /// Gets all commercials in the FinTV library.
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
        var settings = Plugin.Instance?.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
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
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var settings = plugin.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
        settings.Enabled = request.Enabled;
        settings.BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl)
            ? CommercialBrainzSettings.DefaultBaseUrl
            : request.BaseUrl.Trim().TrimEnd('/');
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
        var settings = Plugin.Instance?.Configuration.CommercialBrainz ?? new CommercialBrainzSettings();
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
    /// Syncs commercials from CommercialBrainz using the configured filters.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted when sync starts.</returns>
    [HttpPost("brainz/sync")]
    public async Task<IActionResult> SyncBrainz(CancellationToken cancellationToken)
    {
        await _commercialBrainz.SyncAsync(cancellationToken);
        return Accepted(new { Plugin.Instance?.Configuration.CommercialBrainz?.SyncState });
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
        return Accepted(new { Plugin.Instance?.Configuration.BlackframeTaskState });
    }

    /// <summary>
    /// Gets the current blackframe scan task status.
    /// </summary>
    /// <returns>Task progress state.</returns>
    [HttpGet("scan-status")]
    public ActionResult<object> ScanStatus()
    {
        return Ok(Plugin.Instance?.Configuration.BlackframeTaskState);
    }

    private static object MapBrainzSettings(CommercialBrainzSettings settings)
    {
        return new
        {
            enabled = settings.Enabled,
            baseUrl = settings.BaseUrl,
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
