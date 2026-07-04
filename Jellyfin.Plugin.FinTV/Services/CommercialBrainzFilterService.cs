using System.Text.Json;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;

namespace Jellyfin.Plugin.FinTV.Services;

public class CommercialBrainzFilterService
{
    public bool Matches(CommercialBrainzSettings settings, CommercialBrainzVideoSummary video)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(video.YoutubeUrl))
        {
            return false;
        }

        var tags = NormalizeTags(video.Tags);
        var metadata = video.Metadata ?? new Dictionary<string, JsonElement>();
        var classification = Classify(tags, metadata, video.Visibility, GetAgeLimit(metadata));
        var year = video.Commercial?.Year;
        var decade = video.Commercial?.Decade ?? (year.HasValue ? (year.Value / 10) * 10 : null);
        var brand = video.Advertiser?.Name ?? string.Empty;
        var title = video.Commercial?.Title ?? brand;

        if (settings.MinYear.HasValue && (!year.HasValue || year.Value < settings.MinYear.Value))
        {
            return false;
        }

        if (settings.MaxYear.HasValue && (!year.HasValue || year.Value > settings.MaxYear.Value))
        {
            return false;
        }

        if (settings.Decades.Count > 0)
        {
            if (!decade.HasValue || !settings.Decades.Contains(decade.Value))
            {
                return false;
            }
        }

        if (settings.Brands.Count > 0 && !settings.Brands.Any(brandFilter => MatchesToken(brand, brandFilter) || MatchesToken(title, brandFilter)))
        {
            return false;
        }

        if (settings.Tags.Count > 0 && !settings.Tags.Any(tag => tags.Any(t => MatchesToken(t, tag))))
        {
            return false;
        }

        if (settings.ExcludeTags.Count > 0 && settings.ExcludeTags.Any(tag => tags.Any(t => MatchesToken(t, tag))))
        {
            return false;
        }

        if (settings.Genres.Count > 0)
        {
            var genres = GetMetadataStrings(metadata, "youtube_categories", "genres", "genre");
            if (!settings.Genres.Any(genre => genres.Any(value => MatchesToken(value, genre)) || tags.Any(tag => settings.Genres.Any(genre => MatchesToken(tag, genre)))))
            {
                return false;
            }
        }

        if (settings.Networks.Count > 0 && !settings.Networks.Any(network => MatchesToken(video.Network, network)))
        {
            return false;
        }

        if (settings.ChannelNames.Count > 0 && !settings.ChannelNames.Any(channel => MatchesToken(video.ChannelName, channel)))
        {
            return false;
        }

        var ageLimit = classification.AgeLimit;
        if (settings.MinAgeLimit.HasValue && (!ageLimit.HasValue || ageLimit.Value < settings.MinAgeLimit.Value))
        {
            return false;
        }

        if (settings.MaxAgeLimit.HasValue && (!ageLimit.HasValue || ageLimit.Value > settings.MaxAgeLimit.Value))
        {
            return false;
        }

        if (!settings.AllowBanned && classification.IsBanned)
        {
            return false;
        }

        if (!settings.AllowAdultRated && classification.IsAdultRated)
        {
            return false;
        }

        if (!settings.AllowLateNight && classification.IsLateNight)
        {
            return false;
        }

        if (!settings.AllowSpoof && classification.IsSpoof)
        {
            return false;
        }

        if (!settings.AllowFake && classification.IsFake)
        {
            return false;
        }

        if (!settings.AllowReal && classification.IsReal)
        {
            return false;
        }

        if (!settings.AllowAiEnhanced && classification.IsAiEnhanced)
        {
            return false;
        }

        return true;
    }

    public Commercial MapToCommercial(CommercialBrainzVideoSummary video)
    {
        var tags = NormalizeTags(video.Tags);
        var metadata = video.Metadata ?? new Dictionary<string, JsonElement>();
        var classification = Classify(tags, metadata, video.Visibility, GetAgeLimit(metadata));
        var title = video.Commercial?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = video.Advertiser?.Name ?? video.ChannelName ?? video.YoutubeId ?? "Commercial";
        }

        return new Commercial
        {
            Source = CommercialSource.CommercialBrainz,
            JellyfinItemId = Guid.Empty,
            CommercialBrainzVideoSbid = video.Sbid.ToString("D"),
            YouTubeUrl = video.YoutubeUrl,
            YouTubeVideoId = video.YoutubeId,
            Title = title,
            Duration = TimeSpan.FromMilliseconds(Math.Max(1000, video.DurationMs ?? 30000)),
            Brand = video.Advertiser?.Name,
            Year = video.Commercial?.Year,
            Decade = video.Commercial?.Decade ?? (video.Commercial?.Year is int year ? (year / 10) * 10 : null),
            Network = video.Network,
            ChannelName = video.ChannelName,
            AgeLimit = classification.AgeLimit,
            TagsJson = tags.Count == 0 ? null : FinTvJson.Serialize(tags),
            IsBanned = classification.IsBanned,
            IsAdultRated = classification.IsAdultRated,
            IsLateNight = classification.IsLateNight,
            IsSpoof = classification.IsSpoof,
            IsFake = classification.IsFake,
            IsReal = classification.IsReal,
            IsAiEnhanced = classification.IsAiEnhanced
        };
    }

    private static CommercialClassification Classify(
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, JsonElement> metadata,
        string? visibility,
        int? ageLimit)
    {
        var isSpoof = HasKeyword(tags, metadata, "spoof", "parody");
        var isFake = HasKeyword(tags, metadata, "fake", "fan-made", "fanmade");
        var isAiEnhanced = HasKeyword(tags, metadata, "ai-enhanced", "ai enhanced", "ai_generated", "ai generated", "ai");
        var isLateNight = HasKeyword(tags, metadata, "latenight", "late-night", "late night", "after dark");
        var isBanned = HasKeyword(tags, metadata, "banned", "dmca", "takedown")
            || string.Equals(visibility, "banned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase);
        var isAdultRated = ageLimit >= 18 || HasKeyword(tags, metadata, "adult", "mature", "nsfw");
        var isReal = !isFake && !isSpoof && !isAiEnhanced;

        return new CommercialClassification(
            ageLimit,
            isBanned,
            isAdultRated,
            isLateNight,
            isSpoof,
            isFake,
            isReal,
            isAiEnhanced);
    }

    private static int? GetAgeLimit(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        if (metadata.TryGetValue("youtube_age_limit", out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static List<string> GetMetadataStrings(IReadOnlyDictionary<string, JsonElement> metadata, params string[] keys)
    {
        var results = new List<string>();
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    if (!string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        results.Add(value.GetString()!);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            results.Add(item.GetString()!);
                        }
                    }

                    break;
            }
        }

        return results;
    }

    private static bool HasKeyword(
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, JsonElement> metadata,
        params string[] keywords)
    {
        if (tags.Any(tag => keywords.Any(keyword => MatchesToken(tag, keyword))))
        {
            return true;
        }

        foreach (var keyword in keywords)
        {
            foreach (var pair in metadata)
            {
                if (MatchesToken(pair.Key, keyword))
                {
                    return true;
                }

                if (pair.Value.ValueKind == JsonValueKind.String && MatchesToken(pair.Value.GetString(), keyword))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }

    private static bool MatchesToken(string? value, string? filter)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        return value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CommercialClassification(
        int? AgeLimit,
        bool IsBanned,
        bool IsAdultRated,
        bool IsLateNight,
        bool IsSpoof,
        bool IsFake,
        bool IsReal,
        bool IsAiEnhanced);
}
