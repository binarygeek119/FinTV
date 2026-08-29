using System.Globalization;

namespace ChannelFlow.CommercialDetect;

public static class TimeFormat
{
    public static string Seconds(double? seconds)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || seconds.Value < 0)
        {
            return "—";
        }

        var value = seconds.Value;
        var ts = TimeSpan.FromSeconds(value);
        if (ts.TotalHours >= 1)
        {
            return ts.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        return ts.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    public static string Range(TimeRange range)
        => Seconds(range.StartSeconds) + " – " + Seconds(range.EndSeconds);

    public static string Range(TimeRange? range)
        => range is TimeRange value ? Range(value) : "—";
}
