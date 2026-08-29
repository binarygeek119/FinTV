using System.Globalization;
using System.Text.RegularExpressions;

namespace ChannelFlow.CommercialDetect;

public static partial class BreakChapterNamer
{
    [GeneratedRegex(@"^break(?:\s+(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakNameRegex();

    public static bool IsBreakName(string? name)
        => !string.IsNullOrWhiteSpace(name) && BreakNameRegex().IsMatch(name.Trim());

    public static int HighestIndex(IEnumerable<string?> names)
    {
        var max = 0;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var match = BreakNameRegex().Match(name.Trim());
            if (!match.Success)
            {
                continue;
            }

            var index = match.Groups[1].Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 1;
            if (index > max)
            {
                max = index;
            }
        }

        return max;
    }

    public static IReadOnlyList<string> NextNames(int count, IEnumerable<string?> existing)
    {
        var next = HighestIndex(existing) + 1;
        var names = new List<string>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            names.Add(Format(next + i));
        }

        return names;
    }

    public static string Format(int index)
        => index <= 1 ? "break" : "break " + index.ToString(CultureInfo.InvariantCulture);
}
