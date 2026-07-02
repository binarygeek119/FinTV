using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Emergency Broadcast System settings and custom slate uploads.
/// </summary>
[ApiController]
[Route("FinTV/api/ebs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class EbsController : ControllerBase
{
    private readonly EbsService _ebs;
    private readonly JellyfinCatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="EbsController"/> class.
    /// </summary>
    /// <param name="ebs">EBS service.</param>
    /// <param name="catalog">Jellyfin catalog service.</param>
    public EbsController(EbsService ebs, JellyfinCatalogService catalog)
    {
        _ebs = ebs;
        _catalog = catalog;
    }

    /// <summary>
    /// Gets EBS settings for the admin UI.
    /// </summary>
    /// <returns>EBS settings.</returns>
    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            ebsDisplayMode = (int)(config?.EbsDisplayMode ?? EbsDisplayMode.SlateImage),
            ebsAudioMode = (int)(config?.EbsAudioMode ?? EbsAudioMode.BackgroundMusic),
            ebsSlateVariant = (int)(config?.EbsSlateVariant ?? EbsSlateVariant.Usa),
            ebsBackgroundMusicSource = (int)(config?.EbsBackgroundMusicSource ?? EbsBackgroundMusicSource.NamedLibrary),
            ebsBackgroundMusicLibraryName = config?.EbsBackgroundMusicLibraryName ?? "Background Music",
            ebsBackgroundMusicLibraryId = config?.EbsBackgroundMusicLibraryId ?? string.Empty,
            customSlates = _ebs.GetCustomSlateStatus(),
            stockSlates = new
            {
                usa = EbsService.EbsFolderName + "/offlineusa.jpg",
                international = EbsService.EbsFolderName + "/offline.jpg"
            },
            musicLibraries = _catalog.GetMusicLibraries().Select(l => new { id = l.Id, name = l.Name })
        });
    }

    /// <summary>
    /// Updates EBS settings.
    /// </summary>
    /// <param name="request">Settings payload.</param>
    /// <returns>Updated settings.</returns>
    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] EbsSettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request.EbsDisplayMode.HasValue)
        {
            plugin.Configuration.EbsDisplayMode = request.EbsDisplayMode.Value;
        }

        if (request.EbsAudioMode.HasValue)
        {
            plugin.Configuration.EbsAudioMode = request.EbsAudioMode.Value;
        }

        if (request.EbsSlateVariant.HasValue)
        {
            plugin.Configuration.EbsSlateVariant = request.EbsSlateVariant.Value;
        }

        if (request.EbsBackgroundMusicSource.HasValue)
        {
            plugin.Configuration.EbsBackgroundMusicSource = request.EbsBackgroundMusicSource.Value;
        }

        if (request.EbsBackgroundMusicLibraryName is not null)
        {
            plugin.Configuration.EbsBackgroundMusicLibraryName = request.EbsBackgroundMusicLibraryName.Trim();
        }

        plugin.Configuration.EbsBackgroundMusicLibraryId = string.IsNullOrWhiteSpace(request.EbsBackgroundMusicLibraryId)
            ? null
            : request.EbsBackgroundMusicLibraryId.Trim();

        plugin.SaveConfiguration();
        return GetSettings();
    }

    /// <summary>
    /// Uploads a custom off-air slate image for the USA or International variant.
    /// </summary>
    /// <param name="variant">Slate variant (<c>usa</c> or <c>international</c>).</param>
    /// <param name="file">PNG or JPG image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upload result.</returns>
    [HttpPost("slates/{variant}")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<object>> UploadSlate(
        string variant,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { message = "Image file is required." });
        }

        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa or international." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            await _ebs.UploadCustomSlateAsync(slateVariant, stream, file.FileName, cancellationToken);
            return Ok(new
            {
                variant = slateVariant.ToString(),
                customSlates = _ebs.GetCustomSlateStatus()
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a custom off-air slate image.
    /// </summary>
    /// <param name="variant">Slate variant (<c>usa</c> or <c>international</c>).</param>
    /// <returns>No content.</returns>
    [HttpDelete("slates/{variant}")]
    public ActionResult DeleteSlate(string variant)
    {
        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa or international." });
        }

        _ebs.DeleteCustomSlate(slateVariant);
        return Ok(new { customSlates = _ebs.GetCustomSlateStatus() });
    }

    /// <summary>
    /// Gets a custom slate image for admin preview.
    /// </summary>
    /// <param name="variant">Slate variant (<c>usa</c> or <c>international</c>).</param>
    /// <returns>Image file.</returns>
    [HttpGet("slates/{variant}/image")]
    public ActionResult GetSlateImage(string variant)
    {
        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa or international." });
        }

        var path = _ebs.ResolveCustomSlatePath(slateVariant);
        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        var contentType = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";
        return PhysicalFile(path, contentType);
    }

    private static bool TryParseVariant(string value, out EbsSlateVariant variant)
    {
        if (string.Equals(value, "usa", StringComparison.OrdinalIgnoreCase))
        {
            variant = EbsSlateVariant.Usa;
            return true;
        }

        if (string.Equals(value, "international", StringComparison.OrdinalIgnoreCase))
        {
            variant = EbsSlateVariant.International;
            return true;
        }

        variant = default;
        return false;
    }
}

/// <summary>
/// EBS settings payload.
/// </summary>
public class EbsSettingsRequest
{
    /// <summary>
    /// Gets or sets the off-air video display mode.
    /// </summary>
    public EbsDisplayMode? EbsDisplayMode { get; set; }

    /// <summary>
    /// Gets or sets the off-air audio mode.
    /// </summary>
    public EbsAudioMode? EbsAudioMode { get; set; }

    /// <summary>
    /// Gets or sets which stock slate variant to prefer.
    /// </summary>
    public EbsSlateVariant? EbsSlateVariant { get; set; }

    /// <summary>
    /// Gets or sets where EBS background music is selected from.
    /// </summary>
    public EbsBackgroundMusicSource? EbsBackgroundMusicSource { get; set; }

    /// <summary>
    /// Gets or sets the selected music library name for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the selected music library identifier for EBS background music.
    /// </summary>
    public string? EbsBackgroundMusicLibraryId { get; set; }
}
