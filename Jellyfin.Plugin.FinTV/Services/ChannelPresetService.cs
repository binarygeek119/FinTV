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

    public ChannelPresetService(FinTvDbContext db, ChannelService channels, LogoSetService logoSets)
    {
        _db = db;
        _channels = channels;
        _logoSets = logoSets;
    }

    /// <summary>
    /// Lists presets with whether each channel number already exists.
    /// </summary>
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
        var existingNumbers = await _db.Channels
            .AsNoTracking()
            .Select(c => new { c.Number, c.Id })
            .ToListAsync(cancellationToken);
        LogoSet? logoSet = null;

        if (presets.Any(p => p.UseBinarygeek119Logo))
        {
            try
            {
                logoSet = await _logoSets.EnsureBinarygeek119SetAsync(cancellationToken);
            }
            catch
            {
                logoSet = (await _logoSets.GetAllAsync(cancellationToken))
                    .FirstOrDefault(s => s.Name == ChannelPresets.Binarygeek119LogoSetName);
            }
        }

        var result = new ApplyChannelPresetsResult();
        foreach (var preset in presets)
        {
            var number = preset.GetNumber(numberingMode);
            var match = existingNumbers.FirstOrDefault(c => c.Number == number);
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
                channel.ContentType = preset.ContentType;
                channel.FilterJson = preset.FilterJson;
                channel.Enabled = true;
                ApplyLogo(channel, preset, logoSet);
                ApplyWeatherDefaults(channel, preset);
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
                PlayoutSeed = CreateSeed(number)
            };

            ApplyLogo(newChannel, preset, logoSet);
            ApplyWeatherDefaults(newChannel, preset);

            var created = await _channels.CreateAsync(newChannel, cancellationToken);
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

    private static void ApplyWeatherDefaults(Channel channel, ChannelPresetDefinition preset)
    {
        if (!preset.IsWeatherChannel)
        {
            return;
        }

        channel.WeatherLatitude ??= 41.6057;
        channel.WeatherLongitude ??= -93.5500;
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
