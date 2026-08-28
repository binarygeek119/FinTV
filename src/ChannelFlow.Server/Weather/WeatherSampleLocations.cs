namespace FinTv.Weather;

internal static class WeatherSampleLocations
{
    private static readonly string[] Cities =
    [
        "New York, NY",
        "Los Angeles, CA",
        "Chicago, IL",
        "Houston, TX",
        "Phoenix, AZ",
        "Philadelphia, PA",
        "San Antonio, TX",
        "San Diego, CA",
        "Dallas, TX",
        "San Jose, CA",
        "Austin, TX",
        "Jacksonville, FL",
        "Columbus, OH",
        "Charlotte, NC",
        "Indianapolis, IN",
        "San Francisco, CA",
        "Seattle, WA",
        "Denver, CO",
        "Washington, DC",
        "Boston, MA",
        "Nashville, TN",
        "Portland, OR",
        "Miami, FL",
        "Atlanta, GA",
        "Baltimore, MD",
        "St. Louis, MO",
        "Minneapolis, MN",
        "New Orleans, LA",
        "Salt Lake City, UT",
        "Honolulu, HI",
        "Anchorage, AK",
        "Albuquerque, NM",
        "Boise, ID",
        "Norfolk, VA",
        "Tampa, FL",
        "Kansas City, MO",
        "Cleveland, OH",
        "Pittsburgh, PA",
        "Detroit, MI",
        "Las Vegas, NV"
    ];

    public static bool IsUnsetOrLegacy(string? query)
        => string.IsNullOrWhiteSpace(query);

    public static string PickRandom(IEnumerable<string>? exclude = null)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exclude is not null)
        {
            foreach (var item in exclude)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    skip.Add(item.Trim());
                }
            }
        }

        var options = Cities.Where(city => !skip.Contains(city)).ToArray();
        if (options.Length == 0)
        {
            options = Cities;
        }

        return options[Random.Shared.Next(options.Length)];
    }
}
