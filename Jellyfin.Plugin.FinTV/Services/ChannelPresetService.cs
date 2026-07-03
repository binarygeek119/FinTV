using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Applies ready-made Binarygeek119 channel presets.
/// </summary>
public class ChannelPresetService
{
    private readonly FinTvDbContext _db;
    private readonly ChannelService _channels;
    private readonly LogoSetService _logoSets;
    private readonly LineupGeneratorService _lineupGenerator;

    public ChannelPresetService(
        FinTvDbContext db,
        ChannelService channels,
        LogoSetService logoSets,
        LineupGeneratorService lineupGenerator)
    {
        _db = db;
        _channels = channels;
        _logoSets = logoSets;
        _lineupGenerator = lineupGenerator;
    }

    /// <summary>
    /// Lists presets with whether each channel number already exists.
    /// </summary>
    /// <param name="numberingMode">Legacy or subchannel numbering to display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preset status rows grouped by category.</returns>
    public async Task<IReadOnlyList<ChannelPresetStatus>> GetStatusAsync(
        ChannelPresetNumberingMode numberingMode = ChannelPresetNumberingMode.Legacy,
        CancellationToken cancellationToken = default)
    {
        var existingNumbers = await _db.Channels
            .AsNoTracking()
            .Select(c => c.Number)
            .ToListAsync(cancellationToken);

        return ChannelPresets.All
            .Select(preset =>
            {
                var number = preset.GetNumber(numberingMode);
                return new ChannelPresetStatus
                {
                    Id = preset.Id,
                    Number = number,
                    LegacyNumber = preset.LegacyNumber,
                    SubchannelNumber = preset.SubchannelNumber,
                    Name = preset.Name,
                    Category = preset.Category,
                    Description = preset.Description,
                    ContentType = preset.ContentType,
                    LibraryTag = preset.LibraryTag,
                    NumberingMode = numberingMode,
                    Exists = existingNumbers.Any(n => n == number)
                };
            })
            .ToList();
    }

    /// <summary>
    /// Creates missing preset channels.
    /// </summary>
    /// <param name="request">Apply options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of created and skipped channels.</returns>
    public async Task<ApplyChannelPresetsResult> ApplyAsync(ApplyChannelPresetsRequest request, CancellationToken cancellationToken = default)
    {
        var numberingMode = request.NumberingMode;
        var presets = ResolvePresets(request.PresetIds);
        var existingChannels = await _db.Channels
            .AsNoTracking()
            .Select(c => new { c.Number, c.Id, c.FilterJson })
            .ToListAsync(cancellationToken);

        var existingNumbers = existingChannels
            .Select(c => new { c.Number, c.Id })
            .ToList();

        LogoSet? logoSet = null;
        try
        {
            logoSet = await _logoSets.EnsureBinarygeek119SetAsync(cancellationToken);
        }
        catch
        {
            logoSet = (await _logoSets.GetAllAsync(cancellationToken))
                .FirstOrDefault(s => s.Name == ChannelPresets.Binarygeek119LogoSetName);
        }

        var result = new ApplyChannelPresetsResult();
        foreach (var preset in presets)
        {
            var number = preset.GetNumber(numberingMode);
            var match = existingNumbers.FirstOrDefault(c => c.Number == number)
                ?? existingChannels
                    .Where(c => ChannelMatchesLibraryTag(c.FilterJson, preset.LibraryTag))
                    .Select(c => new { c.Number, c.Id })
                    .FirstOrDefault();
            if (match is not null)
            {
                if (request.SkipExisting && !request.UpdateExisting)
                {
                    result.Skipped.Add(new ChannelPresetActionResult
                    {
                        Id = preset.Id,
                        Number = number,
                        Name = preset.Name,
                        Reason = "Channel number already exists"
                    });
                    continue;
                }

                var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == match.Id, cancellationToken);
                if (channel is null)
                {
                    continue;
                }

                channel.Name = preset.Name;
                channel.Number = number;
                channel.ContentType = preset.ContentType;
                channel.FilterJson = preset.FilterJson;
                channel.CatalogMode = preset.CatalogMode;
                channel.Enabled = true;
                ApplyLogo(channel, preset, logoSet);
                ApplyWeatherDefaults(channel, preset);
                if (channel.ContentType == ChannelContentType.Weather)
                {
                    await BuildWeatherPlayoutAsync(channel, cancellationToken);
                }

                result.Updated.Add(new ChannelPresetActionResult
                {
                    Id = preset.Id,
                    Number = number,
                    Name = preset.Name,
                    ChannelId = channel.Id
                });
                continue;
            }

            var newChannel = new Channel
            {
                Number = number,
                Name = preset.Name,
                ContentType = preset.ContentType,
                Enabled = true,
                FilterJson = preset.FilterJson,
                CatalogMode = preset.CatalogMode,
                PlayoutSeed = CreateSeed(number)
            };

            ApplyLogo(newChannel, preset, logoSet);
            ApplyWeatherDefaults(newChannel, preset);

            var created = await _channels.CreateAsync(newChannel, cancellationToken);
            if (created.ContentType == ChannelContentType.Weather)
            {
                await BuildWeatherPlayoutAsync(created, cancellationToken);
            }

            existingNumbers.Add(new { Number = created.Number, Id = created.Id });
            result.Created.Add(new ChannelPresetActionResult
            {
                Id = preset.Id,
                Number = number,
                Name = preset.Name,
                ChannelId = created.Id
            });
        }

        if (result.Updated.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (logoSet is not null)
        {
            await _logoSets.RepairChannelLogosAsync(logoSet, cancellationToken);
        }

        return result;
    }

    private static IReadOnlyList<ChannelPresetDefinition> ResolvePresets(IReadOnlyList<string>? presetIds)
    {
        if (presetIds is not { Count: > 0 })
        {
            return ChannelPresets.All;
        }

        var resolved = new List<ChannelPresetDefinition>();
        foreach (var id in presetIds)
        {
            var preset = ChannelPresets.Find(id);
            if (preset is not null)
            {
                resolved.Add(preset);
            }
        }

        return resolved;
    }

    private void ApplyLogo(Channel channel, ChannelPresetDefinition preset, LogoSet? logoSet)
    {
        if (!preset.UseBinarygeek119Logo || logoSet is null)
        {
            return;
        }

        _logoSets.ApplyLogoToChannel(channel, logoSet, preset.LogoRelativePath, preset.Name);
    }

    private static bool ChannelMatchesLibraryTag(string? filterJson, string libraryTag)
    {
        var tag = ChannelAiRules.ExtractLibraryTag(filterJson);
        return !string.IsNullOrWhiteSpace(tag)
            && tag.Equals(libraryTag, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyWeatherDefaults(Channel channel, ChannelPresetDefinition preset)
    {
        if (!preset.IsWeatherChannel)
        {
            return;
        }

        channel.WeatherLatitude ??= 41.60574;
        channel.WeatherLongitude ??= -93.55002;
    }

    private async Task BuildWeatherPlayoutAsync(Channel channel, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow.Date;
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
        await _lineupGenerator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
    }

    private static int CreateSeed(decimal number)
    {
        return Math.Abs(decimal.GetBits(number)[0]) + 42;
    }
}

/// <summary>
/// Preset availability row for the admin UI.
/// </summary>
public class ChannelPresetStatus
{
    public string Id { get; set; } = string.Empty;

    public decimal Number { get; set; }

    public decimal LegacyNumber { get; set; }

    public decimal SubchannelNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public ChannelContentType ContentType { get; set; }

    public string LibraryTag { get; set; } = string.Empty;

    public ChannelPresetNumberingMode NumberingMode { get; set; }

    public bool Exists { get; set; }
}

/// <summary>
/// Apply preset request options.
/// </summary>
public class ApplyChannelPresetsRequest
{
    public IReadOnlyList<string>? PresetIds { get; set; }

    public ChannelPresetNumberingMode NumberingMode { get; set; } = ChannelPresetNumberingMode.Legacy;

    public bool SkipExisting { get; set; } = true;

    public bool UpdateExisting { get; set; }
}

/// <summary>
/// Apply preset operation summary.
/// </summary>
public class ApplyChannelPresetsResult
{
    public List<ChannelPresetActionResult> Created { get; set; } = new();

    public List<ChannelPresetActionResult> Updated { get; set; } = new();

    public List<ChannelPresetActionResult> Skipped { get; set; } = new();
}

/// <summary>
/// Result row for a single preset action.
/// </summary>
public class ChannelPresetActionResult
{
    public string Id { get; set; } = string.Empty;

    public decimal Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid? ChannelId { get; set; }

    public string? Reason { get; set; }
}
