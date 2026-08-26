using System.Globalization;

namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Validation and formatting for broadcast-style channel numbers (e.g. 5, 5.1, 5.9).
/// </summary>
public static class ChannelNumbers
{
    /// <summary>
    /// Validates and normalizes a channel number to one decimal place (.0 through .9).
    /// </summary>
    /// <param name="value">The requested channel number.</param>
    /// <param name="normalized">The normalized channel number when valid.</param>
    /// <returns><c>true</c> when the value is valid.</returns>
    public static bool TryNormalize(decimal value, out decimal normalized)
    {
        normalized = Math.Round(value, 1, MidpointRounding.AwayFromZero);

        if (normalized < 1m || normalized != value)
        {
            normalized = default;
            return false;
        }

        var major = decimal.Truncate(normalized);
        var minor = (int)((normalized - major) * 10m);
        if (minor is < 0 or > 9)
        {
            normalized = default;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Formats a channel number for M3U/XMLTV output.
    /// </summary>
    /// <param name="number">The channel number.</param>
    /// <returns>A culture-invariant display string.</returns>
    public static string Format(decimal number)
    {
        var normalized = Math.Round(number, 1, MidpointRounding.AwayFromZero);
        var major = decimal.Truncate(normalized);
        var minor = (int)((normalized - major) * 10m);
        return minor == 0
            ? major.ToString(CultureInfo.InvariantCulture)
            : string.Format(CultureInfo.InvariantCulture, "{0}.{1}", major, minor);
    }
}
