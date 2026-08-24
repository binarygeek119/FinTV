using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FinTv.Services;

namespace FinTv.Weather;

public sealed class WeatherGeocoder
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, (GeoPlace Place, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _http;

    public WeatherGeocoder(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task<GeoPlace> ResolveAsync(string query, CancellationToken cancellationToken)
    {
        var trimmed = WeatherLocationParser.NormalizeLocation(query);
        if (_cache.TryGetValue(trimmed, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
        {
            return hit.Place;
        }

        GeoPlace Remember(GeoPlace resolved)
        {
            _cache[trimmed] = (resolved, DateTimeOffset.UtcNow.Add(CacheTtl));
            return resolved;
        }

        if (WeatherLocationParser.TryParseLatLon(trimmed, out var lat, out var lon))
        {
            var reverse = await SearchAsync($"{lat.ToString("F4", CultureInfo.InvariantCulture)},{lon.ToString("F4", CultureInfo.InvariantCulture)}", cancellationToken);
            if (reverse is not null)
            {
                return Remember(reverse with { Query = trimmed, Latitude = lat, Longitude = lon });
            }

            return Remember(new GeoPlace
            {
                Query = trimmed,
                DisplayName = WeatherLocationParser.GetDisplayName(trimmed),
                Latitude = lat,
                Longitude = lon
            });
        }

        var zip = WeatherLocationParser.ExtractZip(trimmed);
        if (!string.IsNullOrWhiteSpace(zip) && trimmed.Length <= 10)
        {
            var fromZip = await SearchAsync(zip, cancellationToken, country: "US");
            if (fromZip is not null)
            {
                return Remember(fromZip with { Query = trimmed });
            }
        }

        var found = await SearchAsync(trimmed, cancellationToken);
        if (found is not null)
        {
            return Remember(found with { Query = trimmed });
        }

        throw new InvalidOperationException("Could not geocode weather location: " + trimmed);
    }

    private async Task<GeoPlace?> SearchAsync(string name, CancellationToken cancellationToken, string? country = null)
    {
        var client = _http.CreateClient("Weather");
        var url = "https://geocoding-api.open-meteo.com/v1/search?name="
            + Uri.EscapeDataString(name)
            + "&count=1&language=en&format=json";
        if (!string.IsNullOrWhiteSpace(country))
        {
            url += "&country=" + Uri.EscapeDataString(country);
        }

        using var doc = JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        var row = results[0];
        var city = row.TryGetProperty("name", out var n) ? n.GetString() : name;
        var admin = row.TryGetProperty("admin1", out var a) ? a.GetString() : null;
        var countryName = row.TryGetProperty("country", out var c) ? c.GetString() : null;
        var countryCode = row.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
        var display = string.Join(", ", new[] { city, admin, countryName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new GeoPlace
        {
            Query = name,
            DisplayName = display,
            Latitude = row.GetProperty("latitude").GetDouble(),
            Longitude = row.GetProperty("longitude").GetDouble(),
            CountryCode = countryCode,
            Admin1 = admin,
            Timezone = row.TryGetProperty("timezone", out var tz) ? tz.GetString() : null
        };
    }
}
