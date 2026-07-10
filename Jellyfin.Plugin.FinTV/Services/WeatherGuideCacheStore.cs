using System.Text.Json;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// File-backed access to persisted weather guide cache entries.
/// </summary>
internal static class WeatherGuideCacheStore
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = FinTvJson.Options;

    public static int Count()
        => LoadEntries().Count;

    public static void Clear()
    {
        lock (FileLock)
        {
            WriteEntries(new Dictionary<string, WeatherGuideSlotCache>(StringComparer.Ordinal));
        }
    }

    public static bool Contains(string key)
        => LoadEntries().ContainsKey(key);

    public static bool TryGet(string key, out WeatherGuideSlotCache slot)
    {
        var entries = LoadEntries();
        return entries.TryGetValue(key, out slot!);
    }

    public static void Set(string key, WeatherGuideSlotCache slot)
    {
        lock (FileLock)
        {
            var entries = LoadEntriesUnsafe();
            entries[key] = slot;
            WriteEntriesUnsafe(entries);
        }
    }

    public static void SetMany(IEnumerable<KeyValuePair<string, WeatherGuideSlotCache>> values)
    {
        lock (FileLock)
        {
            var entries = LoadEntriesUnsafe();
            foreach (var (key, slot) in values)
            {
                entries[key] = slot;
            }

            WriteEntriesUnsafe(entries);
        }
    }

    private static Dictionary<string, WeatherGuideSlotCache> LoadEntries()
    {
        lock (FileLock)
        {
            return LoadEntriesUnsafe();
        }
    }

    private static Dictionary<string, WeatherGuideSlotCache> LoadEntriesUnsafe()
    {
        var path = GetCacheFilePath();
        if (!File.Exists(path))
        {
            return new Dictionary<string, WeatherGuideSlotCache>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, WeatherGuideSlotCache>(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, WeatherGuideSlotCache>>(json, JsonOptions)
                ?? new Dictionary<string, WeatherGuideSlotCache>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, WeatherGuideSlotCache>(StringComparer.Ordinal);
        }
    }

    private static void WriteEntries(Dictionary<string, WeatherGuideSlotCache> entries)
    {
        lock (FileLock)
        {
            WriteEntriesUnsafe(entries);
        }
    }

    private static void WriteEntriesUnsafe(Dictionary<string, WeatherGuideSlotCache> entries)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
        Directory.CreateDirectory(plugin.DataFolder);
        var path = GetCacheFilePath();
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static string GetCacheFilePath()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("FinTV plugin not initialized.");
        return Path.Combine(plugin.DataFolder, "weather-guide-cache.json");
    }
}
