namespace FinTv.Weather;

public static class WeatherIconMap
{
    public static string FromWmo(int code, bool night = false)
    {
        return code switch
        {
            0 => night ? "Clear" : "Sunny",
            1 => night ? "Mostly-Clear" : "Mostly-Clear",
            2 => "Partly-Cloudy",
            3 => "Cloudy",
            45 or 48 => "Fog",
            51 or 53 or 55 or 80 or 81 => "Shower",
            56 or 57 or 66 or 67 => "Freezing-Rain",
            61 or 63 or 82 => "Rain",
            65 => "Rain",
            71 or 77 => "Light-Snow",
            73 => "Heavy-Snow",
            75 => "Heavy-Snow",
            85 or 86 => "Blowing-Snow",
            95 => night ? "Scattered-Thunderstorms-Night" : "Scattered-Thunderstorms-Day",
            96 or 99 => "Thunderstorm",
            _ => "Cloudy"
        };
    }

    public static string FromWmoText(int code)
    {
        return code switch
        {
            0 => "Clear",
            1 => "Mostly clear",
            2 => "Partly cloudy",
            3 => "Cloudy",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            61 or 63 or 65 => "Rain",
            66 or 67 => "Freezing rain",
            71 or 73 or 75 or 77 => "Snow",
            80 or 81 or 82 => "Showers",
            95 or 96 or 99 => "Thunderstorms",
            _ => "Cloudy"
        };
    }

    public static string FromNwsIcon(string? iconUrl, string? text)
    {
        var hay = ((iconUrl ?? "") + " " + (text ?? "")).ToLowerInvariant();
        if (hay.Contains("tsra") || hay.Contains("thunder"))
        {
            return hay.Contains("night") ? "Scattered-Thunderstorms-Night" : "Scattered-Thunderstorms-Day";
        }

        if (hay.Contains("fzra") || hay.Contains("freezing"))
        {
            return "Freezing-Rain";
        }

        if (hay.Contains("snow") && hay.Contains("rain"))
        {
            return "Rain-Snow";
        }

        if (hay.Contains("blizzard") || hay.Contains("blowing"))
        {
            return "Blowing-Snow";
        }

        if (hay.Contains("snow") || hay.Contains("sn"))
        {
            return hay.Contains("light") ? "Light-Snow" : "Heavy-Snow";
        }

        if (hay.Contains("sleet") || hay.Contains("ip"))
        {
            return "Sleet";
        }

        if (hay.Contains("fog") || hay.Contains("fg"))
        {
            return "Fog";
        }

        if (hay.Contains("smoke"))
        {
            return "Smoke";
        }

        if (hay.Contains("shra") || hay.Contains("shower"))
        {
            return "Shower";
        }

        if (hay.Contains("ra") || hay.Contains("rain"))
        {
            return "Rain";
        }

        if (hay.Contains("ovc") || hay.Contains("bkn") || hay.Contains("cloudy"))
        {
            return "Cloudy";
        }

        if (hay.Contains("sct") || hay.Contains("partly"))
        {
            return "Partly-Cloudy";
        }

        if (hay.Contains("few") || hay.Contains("mostly"))
        {
            return "Mostly-Clear";
        }

        if (hay.Contains("skc") || hay.Contains("sunny") || hay.Contains("clear"))
        {
            return hay.Contains("night") ? "Clear" : "Sunny";
        }

        if (hay.Contains("wind"))
        {
            return "Windy";
        }

        return "Cloudy";
    }

    public static string DisplayName(string? iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey) || string.Equals(iconKey, "No-Data", StringComparison.OrdinalIgnoreCase))
        {
            return "Local weather";
        }

        return iconKey.Replace("-", " ", StringComparison.Ordinal);
    }

    public static string Cardinal(double degrees)
    {
        var dirs = new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        var index = (int)Math.Round(degrees / 22.5) & 15;
        return dirs[index];
    }
}
