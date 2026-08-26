using FinTv.Services;

namespace FinTv.Weather;

public sealed class WeatherStarSequencer
{
    private readonly IReadOnlyList<Slot> _slots;
    public readonly WeatherStarDockerVariant Skin;
    public readonly bool Wide;

    public WeatherStarSequencer(
        string? permalinkQuery,
        WeatherStarDockerVariant skin,
        bool wide,
        bool hasAlerts = true,
        int localForecastPages = 1)
    {
        Skin = skin;
        Wide = wide;
        var flags = Parse(permalinkQuery);
        var speed = 1.0;
        if (flags.TryGetValue("speed", out var speedRaw) && double.TryParse(speedRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            speed = Math.Clamp(parsed, 0.5, 2.0);
        }

        var screen = TimeSpan.FromSeconds(16 * speed);
        var week = TimeSpan.FromSeconds(22 * speed);
        var travel = TimeSpan.FromSeconds(40 * speed);
        var slots = new List<Slot>();
        if (hasAlerts)
        {
            Add(slots, flags, "hazards", WeatherStarScreen.Hazards, screen);
        }

        Add(slots, flags, "current-weather", WeatherStarScreen.Current, screen);
        Add(slots, flags, "latest-observations", WeatherStarScreen.Observations, screen);
        Add(slots, flags, "hourly", WeatherStarScreen.Hourly, screen);
        Add(slots, flags, "hourly-graph", WeatherStarScreen.HourlyGraph, screen);
        if (Flag(flags, "local-forecast", true))
        {
            var pages = Math.Clamp(localForecastPages, 1, 6);
            for (var i = 0; i < pages; i++)
            {
                slots.Add(new Slot(WeatherStarScreen.LocalForecast, week));
            }
        }

        if (Flag(flags, "extended-forecast", true))
        {
            slots.Add(new Slot(WeatherStarScreen.ExtendedForecast, week));
            slots.Add(new Slot(WeatherStarScreen.ExtendedForecast, week));
        }
        Add(slots, flags, "regional-forecast", WeatherStarScreen.Regional, screen);
        Add(slots, flags, "travel", WeatherStarScreen.Travel, travel);
        Add(slots, flags, "almanac", WeatherStarScreen.Almanac, screen);
        Add(slots, flags, "spc-outlook", WeatherStarScreen.SpcOutlook, screen);
        Add(slots, flags, "radar", WeatherStarScreen.Radar, screen);
        _slots = slots.Count == 0
            ? [new Slot(WeatherStarScreen.Current, screen), new Slot(WeatherStarScreen.LocalForecast, week)]
            : slots;
    }

    public (WeatherStarScreen Screen, int RadarIndex, int Repeat) At(TimeSpan elapsed)
    {
        if (_slots.Count == 0)
        {
            return (WeatherStarScreen.Current, 0, 0);
        }

        var cycle = 0.0;
        foreach (var slot in _slots)
        {
            cycle += slot.Duration.TotalMilliseconds;
        }

        var pos = elapsed.TotalMilliseconds % cycle;
        if (pos < 0)
        {
            pos += cycle;
        }

        var acc = 0.0;
        for (var i = 0; i < _slots.Count; i++)
        {
            var next = acc + _slots[i].Duration.TotalMilliseconds;
            if (pos < next || i == _slots.Count - 1)
            {
                var within = Math.Max(0, pos - acc);
                var radarIndex = (int)(within / 400);
                var repeat = 0;
                for (var j = 0; j < i; j++)
                {
                    if (_slots[j].Screen == _slots[i].Screen)
                    {
                        repeat++;
                    }
                }

                return (_slots[i].Screen, radarIndex, repeat);
            }

            acc = next;
        }

        return (_slots[0].Screen, 0, 0);
    }

    private static void Add(
        List<Slot> slots,
        Dictionary<string, string> flags,
        string key,
        WeatherStarScreen screen,
        TimeSpan duration)
    {
        if (Flag(flags, key, true))
        {
            slots.Add(new Slot(screen, duration));
        }
    }

    private static bool Flag(Dictionary<string, string> flags, string key, bool fallback)
    {
        if (!flags.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        return raw is not "false" and not "0";
    }

    private static Dictionary<string, string> Parse(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.Trim().TrimStart('?');
        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = segment.IndexOf('=');
            if (sep < 0)
            {
                result[Uri.UnescapeDataString(segment)] = "true";
                continue;
            }

            result[Uri.UnescapeDataString(segment[..sep])] = Uri.UnescapeDataString(segment[(sep + 1)..]);
        }

        return result;
    }

    private readonly record struct Slot(WeatherStarScreen Screen, TimeSpan Duration);
}
