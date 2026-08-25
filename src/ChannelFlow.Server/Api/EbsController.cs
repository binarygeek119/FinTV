using FinTv.Configuration;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

/// <summary>
/// Off Air settings and custom slate uploads.
/// </summary>
[ApiController]
[Route("api/ebs")]
[Authorize(Policy = "admin")]
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
        var config = FinTvRuntime.Current?.Configuration;
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
                usa = EbsService.EbsFolderName + "/offline_usa_16_9.jpg",
                usa16x9 = EbsService.EbsFolderName + "/offline_usa_16_9.jpg",
                usa4x3 = EbsService.EbsFolderName + "/offline_usa_4_3.jpg",
                world = EbsService.EbsFolderName + "/offline_world_16_9.jpg",
                world16x9 = EbsService.EbsFolderName + "/offline_world_16_9.jpg",
                world4x3 = EbsService.EbsFolderName + "/offline_world_4_3.jpg",
                international = EbsService.EbsFolderName + "/offline_world_16_9.jpg"
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
        var plugin = FinTvRuntime.Current;
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
    /// Uploads a custom off-air slate image for the USA or World variant.
    /// </summary>
    /// <param name="variant">Slate variant (<c>usa</c>, <c>world</c>, or <c>international</c>).</param>
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
            return BadRequest(new { message = "Variant must be usa, world, or international." });
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
    /// <param name="variant">Slate variant (<c>usa</c>, <c>world</c>, or <c>international</c>).</param>
    /// <returns>No content.</returns>
    [HttpDelete("slates/{variant}")]
    public ActionResult DeleteSlate(string variant)
    {
        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa, world, or international." });
        }

        _ebs.DeleteCustomSlate(slateVariant);
        return Ok(new { customSlates = _ebs.GetCustomSlateStatus() });
    }

    /// <summary>
    /// Gets the effective off-air slate (custom upload or bundled stock) for admin preview.
    /// </summary>
    /// <param name="variant">Slate variant (<c>usa</c>, <c>world</c>, or <c>international</c>).</param>
    /// <param name="aspect">Channel aspect: <c>0</c> 16:9, <c>1</c> 4:3.</param>
    /// <returns>Image file.</returns>
    [HttpGet("slates/{variant}/image")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ActionResult GetSlateImage(string variant, [FromQuery] int? aspect)
    {
        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa, world, or international." });
        }

        return FileSlate(_ebs.ResolveSlatePath(slateVariant, ParseAspect(aspect)));
    }

    /// <summary>
    /// Gets the off-air image currently used for dead air and playback errors.
    /// </summary>
    /// <param name="variant">Optional slate variant override (<c>0</c> USA, <c>1</c> World).</param>
    /// <param name="aspect">Channel aspect: <c>0</c> 16:9, <c>1</c> 4:3.</param>
    /// <returns>Image file.</returns>
    [HttpGet("preview")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ActionResult GetPreview([FromQuery] int? variant, [FromQuery] int? aspect)
    {
        var slateVariant = variant switch
        {
            1 => EbsSlateVariant.International,
            0 => EbsSlateVariant.Usa,
            _ => FinTvRuntime.Current?.Configuration.EbsSlateVariant ?? EbsSlateVariant.Usa
        };

        return FileSlate(_ebs.ResolveSlatePath(slateVariant, ParseAspect(aspect)));
    }

    private ActionResult FileSlate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, GetImageContentType(path));
    }

    private static string GetImageContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    private static bool TryParseVariant(string value, out EbsSlateVariant variant)
    {
        if (string.Equals(value, "usa", StringComparison.OrdinalIgnoreCase))
        {
            variant = EbsSlateVariant.Usa;
            return true;
        }

        if (string.Equals(value, "international", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "world", StringComparison.OrdinalIgnoreCase))
        {
            variant = EbsSlateVariant.International;
            return true;
        }

        variant = default;
        return false;
    }

    private static AspectRatioMode ParseAspect(int? aspect)
        => aspect == (int)AspectRatioMode.FourThree
            ? AspectRatioMode.FourThree
            : AspectRatioMode.SixteenNine;
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
