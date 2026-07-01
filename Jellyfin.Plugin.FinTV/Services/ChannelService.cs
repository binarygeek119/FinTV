using System.Text.Json;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class ChannelService
{
    private readonly FinTvDbContext _db;

    public ChannelService(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task<List<Channel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Channels
            .Include(c => c.DefaultLineup)
            .Include(c => c.LogoSet)
            .OrderBy(c => c.Number)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Channel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Channels
            .Include(c => c.DefaultLineup!)
                .ThenInclude(l => l.Slots)
                .ThenInclude(s => s.Candidates)
            .Include(c => c.Overrides)
                .ThenInclude(o => o.Slots)
                .ThenInclude(s => s.Candidates)
            .Include(c => c.LogoSet)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Channel?> GetByNumberAsync(int number, CancellationToken cancellationToken = default)
    {
        return await _db.Channels.FirstOrDefaultAsync(c => c.Number == number && c.Enabled, cancellationToken);
    }

    public async Task<Channel> CreateAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        channel.DefaultLineup = new Lineup
        {
            ChannelId = channel.Id,
            Name = "Default",
            IsDefault = true,
            Slots = CreateEmptySlots()
        };

        _db.Channels.Add(channel);
        await _db.SaveChangesAsync(cancellationToken);
        return channel;
    }

    public async Task<Channel?> UpdateAsync(Guid id, Channel updated, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        existing.Number = updated.Number;
        existing.Name = updated.Name;
        existing.Enabled = updated.Enabled;
        existing.ContentType = updated.ContentType;
        existing.AspectRatio = updated.AspectRatio;
        existing.ScanlinesEnabled = updated.ScanlinesEnabled;
        existing.LogoSetId = updated.LogoSetId;
        existing.ChannelLogoPath = updated.ChannelLogoPath;
        existing.LogoFileName = updated.LogoFileName;
        existing.BugPlacement = updated.BugPlacement;
        existing.CommercialPresetId = updated.CommercialPresetId;
        existing.AudioLanguage = updated.AudioLanguage;
        existing.PlayoutSeed = updated.PlayoutSeed;
        existing.WeatherLatitude = updated.WeatherLatitude;
        existing.WeatherLongitude = updated.WeatherLongitude;
        existing.FilterJson = updated.FilterJson;

        await _db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Channels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        _db.Channels.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static List<LineupSlot> CreateEmptySlots()
    {
        return Enumerable.Range(0, 48)
            .Select(i => new LineupSlot { SlotIndex = i, Candidates = new List<SlotCandidate>() })
            .ToList();
    }

    public async Task SaveAnchorAsync(Guid channelId, object anchor, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return;
        }

        channel.PlayoutAnchorJson = JsonSerializer.Serialize(anchor);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<T?> GetAnchorAsync<T>(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null || string.IsNullOrWhiteSpace(channel.PlayoutAnchorJson))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(channel.PlayoutAnchorJson);
    }
}
