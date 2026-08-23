using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FinTv.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FinTv.Weather;

public sealed class WeatherDataClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(8);
    private readonly ConcurrentDictionary<string, (WeatherSnapshot Snap, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _http;
    private readonly WeatherGeocoder _geocoder;
    private readonly ILogger<WeatherDataClient> _logger;

    public WeatherDataClient(IHttpClientFactory http, WeatherGeocoder geocoder, ILogger<WeatherDataClient> logger)
    {
        _http = http;
        _geocoder = geocoder;
        _logger = logger;
    }

    public async Task<WeatherSnapshot> GetSnapshotAsync(
        string locationQuery,
        WeatherSourceKind source,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        var place = await _geocoder.ResolveAsync(locationQuery, cancellationToken);
        var isUs = IsUnitedStates(place, locationQuery);
        var backend = ResolveBackend(source, isUs);
        var cacheKey = $"{backend}|{place.Latitude:F3}|{place.Longitude:F3}|{(useMetric ? "si" : "us")}";
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
        {
            return hit.Snap;
        }

        WeatherSnapshot snap;
        try
        {
            snap = backend == "noaa"
                ? await FetchNoaaAsync(place, useMetric, cancellationToken)
                : await FetchOpenMeteoAsync(place, isUs, useMetric, cancellationToken);
        }
        catch (Exception ex) when (backend == "noaa" && ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NOAA weather failed for {Place}; using Open-Meteo", place.DisplayName);
            snap = await FetchOpenMeteoAsync(place, isUs, useMetric, cancellationToken);
        }

        _cache[cacheKey] = (snap, DateTimeOffset.UtcNow.Add(CacheTtl));
        return snap;
    }

    public static WeatherSourceKind ParseSource(string? value)
    {
        if (string.Equals(value, "us", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "unitedstates", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "noaa", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherSourceKind.UnitedStates;
        }

        if (string.Equals(value, "world", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "openmeteo", StringComparison.OrdinalIgnoreCase))
        {
            return WeatherSourceKind.World;
        }

        return WeatherSourceKind.Auto;
    }

    private static string ResolveBackend(WeatherSourceKind source, bool isUs)
        => source switch
        {
            WeatherSourceKind.UnitedStates => "noaa",
            WeatherSourceKind.World => "open-meteo",
            _ => isUs ? "noaa" : "open-meteo"
        };

    private static bool IsUnitedStates(GeoPlace place, string query)
    {
        if (string.Equals(place.CountryCode, "US", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return WeatherLocationParser.ExtractZip(query) is not null && query.Length <= 12;
    }

    private HttpClient Client() => _http.CreateClient("Weather");

    private async Task<WeatherSnapshot> FetchOpenMeteoAsync(
        GeoPlace place,
        bool isUs,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        var temp = useMetric ? "celsius" : "fahrenheit";
        var wind = useMetric ? "kmh" : "mph";
        var url =
            "https://api.open-meteo.com/v1/forecast?latitude="
            + place.Latitude.ToString(CultureInfo.InvariantCulture)
            + "&longitude=" + place.Longitude.ToString(CultureInfo.InvariantCulture)
            + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_gusts_10m,wind_direction_10m,dew_point_2m,surface_pressure,visibility"
            + "&hourly=temperature_2m,apparent_temperature,weather_code,precipitation_probability,wind_speed_10m,wind_direction_10m,dew_point_2m,cloud_cover"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
            + "&temperature_unit=" + temp
            + "&wind_speed_unit=" + wind
            + "&timezone=auto";
        using var doc = JsonDocument.Parse(await Client().GetStringAsync(url, cancellationToken));
        var root = doc.RootElement;
        var current = root.GetProperty("current");
        var code = current.GetProperty("weather_code").GetInt32();
        var night = DateTimeOffset.UtcNow.Hour is >= 0 and < 6 or >= 20;
        var visMeters = GetDouble(current, "visibility");
        var pressureHpa = GetDouble(current, "surface_pressure");
        var temperature = current.GetProperty("temperature_2m").GetDouble();
        var feels = GetDouble(current, "apparent_temperature");
        string? apparentLabel = null;
        if (feels is double apparent)
        {
            if (apparent > temperature + 0.5)
            {
                apparentLabel = "Heat Index:";
            }
            else if (apparent < temperature - 0.5)
            {
                apparentLabel = "Wind Chill:";
            }
        }

        var snapCurrent = new WeatherCurrent
        {
            IconKey = WeatherIconMap.FromWmo(code, night),
            ConditionText = WeatherIconMap.FromWmoText(code),
            Temperature = temperature,
            FeelsLike = feels,
            ApparentLabel = apparentLabel,
            Dewpoint = GetDouble(current, "dew_point_2m"),
            Humidity = GetInt(current, "relative_humidity_2m"),
            WindSpeed = GetDouble(current, "wind_speed_10m"),
            WindGust = GetDouble(current, "wind_gusts_10m"),
            WindDirection = current.TryGetProperty("wind_direction_10m", out var wd) ? WeatherIconMap.Cardinal(wd.GetDouble()) : null,
            Pressure = pressureHpa is null ? null : useMetric ? pressureHpa : pressureHpa * 0.02953,
            Visibility = visMeters is null ? null : useMetric ? visMeters / 1000 : visMeters / 1609.34,
            StationName = place.DisplayName
        };

        var hourly = ReadUpcomingHourly(root, maxCount: 48);

        var daily = new List<WeatherDaily>();
        if (root.TryGetProperty("daily", out var dailyEl))
        {
            var times = dailyEl.GetProperty("time");
            var max = dailyEl.GetProperty("temperature_2m_max");
            var min = dailyEl.GetProperty("temperature_2m_min");
            var codes = dailyEl.GetProperty("weather_code");
            var count = Math.Min(7, times.GetArrayLength());
            for (var i = 0; i < count; i++)
            {
                var date = DateOnly.Parse(times[i].GetString()!, CultureInfo.InvariantCulture);
                daily.Add(new WeatherDaily
                {
                    Date = date,
                    Name = i == 0 ? "Today" : date.ToDateTime(TimeOnly.MinValue).ToString("dddd"),
                    IconKey = WeatherIconMap.FromWmo(codes[i].GetInt32()),
                    Narrative = WeatherIconMap.FromWmoText(codes[i].GetInt32()),
                    High = max[i].GetDouble(),
                    Low = min[i].GetDouble()
                });
            }
        }

        var observations = await TryNearbyObservationsAsync(place, isUs, useMetric, cancellationToken);
        if (observations.Count == 0 && snapCurrent is not null)
        {
            observations = [ToObservationRow(snapCurrent, useMetric)];
        }

        var periods = daily.Select(day => new WeatherForecastPeriod
        {
            Name = day.Name,
            Narrative = day.Narrative
                + ". High " + Math.Round(day.High ?? 0).ToString("0", CultureInfo.InvariantCulture)
                + ", low " + Math.Round(day.Low ?? 0).ToString("0", CultureInfo.InvariantCulture) + ".",
            IsDaytime = true,
            Temperature = day.High ?? day.Low ?? 0,
            IconKey = day.IconKey
        }).ToList();

        return new WeatherSnapshot
        {
            Place = place,
            IsUnitedStates = isUs,
            Backend = "open-meteo",
            UseMetric = useMetric,
            Current = snapCurrent,
            Hourly = hourly,
            Daily = daily,
            Observations = observations,
            Periods = periods,
            Regional = await BuildRegionalCitiesAsync(observations, daily, place, useMetric, cancellationToken),
            Travel = await FetchTravelCitiesAsync(useMetric, cancellationToken),
            SpcOutlook = isUs
                ? await FetchSpcOutlookAsync(place, cancellationToken)
                : [],
            Alerts = isUs
                ? await FetchAlertsAsync(Client(), place.Latitude, place.Longitude, cancellationToken)
                : [],
            Radar = isUs
                ? await FetchRadarAsync(Client(), place, cancellationToken)
                : [],
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<WeatherSnapshot> FetchNoaaAsync(GeoPlace place, bool useMetric, CancellationToken cancellationToken)
    {
        var client = Client();
        var lat = place.Latitude.ToString("F4", CultureInfo.InvariantCulture);
        var lon = place.Longitude.ToString("F4", CultureInfo.InvariantCulture);
        using var pointsDoc = JsonDocument.Parse(
            await client.GetStringAsync($"https://api.weather.gov/points/{lat},{lon}", cancellationToken));
        var props = pointsDoc.RootElement.GetProperty("properties");
        var forecastUrl = props.GetProperty("forecast").GetString();
        var hourlyUrl = props.GetProperty("forecastHourly").GetString();
        var stationsUrl = props.TryGetProperty("observationStations", out var st) ? st.GetString() : null;
        var relative = props.TryGetProperty("relativeLocation", out var rel) ? rel : default;
        var city = relative.ValueKind == JsonValueKind.Object
            && relative.TryGetProperty("properties", out var rp)
            && rp.TryGetProperty("city", out var cityEl)
            ? cityEl.GetString()
            : place.DisplayName;
        var state = relative.ValueKind == JsonValueKind.Object
            && relative.TryGetProperty("properties", out var rp2)
            && rp2.TryGetProperty("state", out var stateEl)
            ? stateEl.GetString()
            : place.Admin1;
        var named = new GeoPlace
        {
            Query = place.Query,
            DisplayName = string.Join(", ", new[] { city, state }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Latitude = place.Latitude,
            Longitude = place.Longitude,
            CountryCode = "US",
            Admin1 = state,
            Timezone = place.Timezone
        };

        WeatherCurrent? current = null;
        IReadOnlyList<WeatherStationObservation> observations = [];
        if (!string.IsNullOrWhiteSpace(stationsUrl))
        {
            var bundle = await FetchStationObservationsAsync(client, stationsUrl, useMetric, cancellationToken);
            current = bundle.Current;
            observations = bundle.Nearby;
        }

        var daily = new List<WeatherDaily>();
        var periods = new List<WeatherForecastPeriod>();
        if (!string.IsNullOrWhiteSpace(forecastUrl))
        {
            using var forecastDoc = JsonDocument.Parse(await client.GetStringAsync(forecastUrl, cancellationToken));
            var byDay = new Dictionary<string, WeatherDaily>(StringComparer.OrdinalIgnoreCase);
            foreach (var period in forecastDoc.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
            {
                if (period.TryGetProperty("endTime", out var endEl)
                    && DateTimeOffset.TryParse(endEl.GetString(), out var end)
                    && end <= DateTimeOffset.UtcNow)
                {
                    continue;
                }

                var name = period.GetProperty("name").GetString() ?? "";
                var isDay = !period.TryGetProperty("isDaytime", out var dayEl) || dayEl.GetBoolean();
                var temp = period.GetProperty("temperature").GetDouble();
                if (useMetric)
                {
                    temp = (temp - 32) * 5 / 9;
                }

                var iconKey = WeatherIconMap.FromNwsIcon(
                    period.TryGetProperty("icon", out var icon) ? icon.GetString() : null,
                    period.GetProperty("shortForecast").GetString());
                var narrative = period.GetProperty("detailedForecast").GetString()
                    ?? period.GetProperty("shortForecast").GetString()
                    ?? "";
                periods.Add(new WeatherForecastPeriod
                {
                    Name = name,
                    Narrative = narrative,
                    IsDaytime = isDay,
                    Temperature = temp,
                    IconKey = iconKey
                });

                var dayName = name.Replace(" Night", "", StringComparison.OrdinalIgnoreCase);
                if (!byDay.TryGetValue(dayName, out var existing))
                {
                    byDay[dayName] = new WeatherDaily
                    {
                        Date = DateOnly.FromDateTime(period.GetProperty("startTime").GetDateTime()),
                        Name = dayName,
                        IconKey = iconKey,
                        Narrative = narrative,
                        High = isDay ? temp : null,
                        Low = isDay ? null : temp
                    };
                }
                else if (isDay)
                {
                    byDay[dayName] = new WeatherDaily
                    {
                        Date = existing.Date,
                        Name = existing.Name,
                        IconKey = iconKey,
                        Narrative = narrative,
                        High = temp,
                        Low = existing.Low
                    };
                }
                else
                {
                    byDay[dayName] = new WeatherDaily
                    {
                        Date = existing.Date,
                        Name = existing.Name,
                        IconKey = existing.IconKey,
                        Narrative = existing.Narrative,
                        High = existing.High,
                        Low = temp
                    };
                }

                if (periods.Count >= 14)
                {
                    break;
                }
            }

            daily.AddRange(byDay.Values);
            current ??= daily.Count > 0
                ? new WeatherCurrent
                {
                    IconKey = daily[0].IconKey,
                    ConditionText = daily[0].Narrative.Split('.')[0],
                    Temperature = daily[0].High ?? daily[0].Low ?? 0,
                    StationName = named.DisplayName
                }
                : null;
        }

        var hourly = new List<WeatherHourly>();
        if (!string.IsNullOrWhiteSpace(hourlyUrl))
        {
            using var hourlyDoc = JsonDocument.Parse(await client.GetStringAsync(hourlyUrl, cancellationToken));
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
            foreach (var period in hourlyDoc.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
            {
                var start = period.GetProperty("startTime").GetDateTimeOffset();
                if (start < cutoff)
                {
                    continue;
                }

                var temp = period.GetProperty("temperature").GetDouble();
                if (useMetric)
                {
                    temp = (temp - 32) * 5 / 9;
                }

                var dew = ConvertNwsTemperature(period, "dewpoint", useMetric);
                var (windSpeed, windDir) = ReadNwsHourlyWind(period, useMetric);
                var humidity = GetUnitValue(period, "relativeHumidity") is double rh
                    ? (int)Math.Round(rh)
                    : (int?)null;
                hourly.Add(new WeatherHourly
                {
                    Time = start,
                    Temperature = temp,
                    FeelsLike = ConvertNwsTemperature(period, "apparentTemperature", useMetric)
                        ?? ApparentTemperature(temp, humidity, windSpeed, useMetric),
                    Dewpoint = dew,
                    WindSpeed = windSpeed,
                    WindDirection = windDir,
                    IconKey = WeatherIconMap.FromNwsIcon(period.TryGetProperty("icon", out var icon) ? icon.GetString() : null, period.GetProperty("shortForecast").GetString()),
                    ConditionText = period.GetProperty("shortForecast").GetString(),
                    PrecipitationChance = period.TryGetProperty("probabilityOfPrecipitation", out var pop)
                        && pop.TryGetProperty("value", out var pv)
                        && pv.ValueKind == JsonValueKind.Number
                            ? pv.GetInt32()
                            : null
                });
                if (hourly.Count >= 48)
                {
                    break;
                }
            }
        }

        var alerts = await FetchAlertsAsync(client, named.Latitude, named.Longitude, cancellationToken);

        var radar = await FetchRadarAsync(client, named, cancellationToken);

        return new WeatherSnapshot
        {
            Place = named,
            IsUnitedStates = true,
            Backend = "noaa",
            UseMetric = useMetric,
            Current = current,
            Hourly = hourly,
            Daily = daily,
            Alerts = alerts,
            Radar = radar,
            Observations = observations,
            Periods = periods,
            Regional = await BuildRegionalCitiesAsync(observations, daily, named, useMetric, cancellationToken),
            Travel = await FetchTravelCitiesAsync(useMetric, cancellationToken),
            SpcOutlook = await FetchSpcOutlookAsync(named, cancellationToken),
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<IReadOnlyList<WeatherStationObservation>> TryNearbyObservationsAsync(
        GeoPlace place,
        bool isUs,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        if (!isUs)
        {
            return [];
        }

        try
        {
            var client = Client();
            var lat = place.Latitude.ToString("F4", CultureInfo.InvariantCulture);
            var lon = place.Longitude.ToString("F4", CultureInfo.InvariantCulture);
            using var pointsDoc = JsonDocument.Parse(
                await client.GetStringAsync($"https://api.weather.gov/points/{lat},{lon}", cancellationToken));
            var stationsUrl = pointsDoc.RootElement.GetProperty("properties").TryGetProperty("observationStations", out var st)
                ? st.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(stationsUrl))
            {
                return [];
            }

            return (await FetchStationObservationsAsync(client, stationsUrl, useMetric, cancellationToken)).Nearby;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nearby observation stations unavailable");
            return [];
        }
    }

    private async Task<(WeatherCurrent? Current, List<WeatherStationObservation> Nearby)> FetchStationObservationsAsync(
        HttpClient client,
        string stationsUrl,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stationsDoc = JsonDocument.Parse(await client.GetStringAsync(stationsUrl, cancellationToken));
            var features = stationsDoc.RootElement.GetProperty("features");
            var candidates = new List<(string Id, string City, double? Lat, double? Lon)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in features.EnumerateArray())
            {
                if (candidates.Count >= 10)
                {
                    break;
                }

                if (!feature.TryGetProperty("properties", out var props))
                {
                    continue;
                }

                var id = props.TryGetProperty("stationIdentifier", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                var name = props.TryGetProperty("name", out var nm) ? nm.GetString() : id;
                double? lat = null;
                double? lon = null;
                if (feature.TryGetProperty("geometry", out var geo)
                    && geo.ValueKind == JsonValueKind.Object
                    && geo.TryGetProperty("coordinates", out var coords)
                    && coords.ValueKind == JsonValueKind.Array
                    && coords.GetArrayLength() >= 2)
                {
                    lon = coords[0].GetDouble();
                    lat = coords[1].GetDouble();
                }

                candidates.Add((id, ShortStationCity(name, id), lat, lon));
            }

            var parsed = await Task.WhenAll(
                candidates.Select(c => TryParseStationAsync(client, c.Id, c.City, c.Lat, c.Lon, useMetric, cancellationToken)));
            var nearby = new List<WeatherStationObservation>();
            WeatherCurrent? current = null;
            foreach (var item in parsed)
            {
                if (item is null)
                {
                    continue;
                }

                current ??= item.Current;
                if (nearby.Count < 7)
                {
                    nearby.Add(item.Row);
                }
            }

            return (current, nearby);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NWS station list fetch failed");
            return (null, []);
        }
    }

    private async Task<IReadOnlyList<WeatherRegionalCity>> BuildRegionalCitiesAsync(
        IReadOnlyList<WeatherStationObservation> nearby,
        IReadOnlyList<WeatherDaily> daily,
        GeoPlace place,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        var source = nearby.Where(row => row.Latitude is not null && row.Longitude is not null).Take(6).ToList();
        if (source.Count == 0)
        {
            source = nearby.Take(6).ToList();
        }

        var cities = source.Select(row => new WeatherRegionalCity
        {
            Name = row.Location,
            IconKey = row.IconKey,
            High = row.Temperature
        }).ToList();

        if (cities.Count == 0 && daily.Count > 0)
        {
            cities.Add(new WeatherRegionalCity
            {
                Name = place.DisplayName,
                IconKey = daily[0].IconKey,
                High = daily[0].High,
                Low = daily[0].Low
            });
        }

        var withCoords = source.Where(row => row.Latitude is not null && row.Longitude is not null).ToList();
        if (withCoords.Count == 0)
        {
            return cities;
        }

        try
        {
            var lats = string.Join(",", withCoords.Select(c => c.Latitude!.Value.ToString("F3", CultureInfo.InvariantCulture)));
            var lons = string.Join(",", withCoords.Select(c => c.Longitude!.Value.ToString("F3", CultureInfo.InvariantCulture)));
            var unit = useMetric ? "celsius" : "fahrenheit";
            var url =
                "https://api.open-meteo.com/v1/forecast?latitude=" + lats
                + "&longitude=" + lons
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                + "&temperature_unit=" + unit
                + "&forecast_days=1";
            using var doc = JsonDocument.Parse(await Client().GetStringAsync(url, cancellationToken));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var count = Math.Min(root.GetArrayLength(), withCoords.Count);
                for (var i = 0; i < count; i++)
                {
                    cities[i] = ReadRegionalDaily(root[i], withCoords[i]);
                }
            }
            else if (cities.Count > 0)
            {
                cities[0] = ReadRegionalDaily(root, withCoords[0]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Regional city forecasts unavailable");
        }

        return cities;
    }

    private static WeatherRegionalCity ReadRegionalDaily(JsonElement root, WeatherStationObservation station)
    {
        if (!root.TryGetProperty("daily", out var daily)
            || !daily.TryGetProperty("temperature_2m_max", out var max)
            || max.GetArrayLength() == 0)
        {
            return new WeatherRegionalCity
            {
                Name = station.Location,
                IconKey = station.IconKey,
                High = station.Temperature
            };
        }

        var min = daily.TryGetProperty("temperature_2m_min", out var minEl) && minEl.GetArrayLength() > 0
            ? minEl[0].GetDouble()
            : (double?)null;
        var icon = station.IconKey;
        if (daily.TryGetProperty("weather_code", out var codes) && codes.GetArrayLength() > 0)
        {
            icon = WeatherIconMap.FromWmo(codes[0].GetInt32());
        }

        return new WeatherRegionalCity
        {
            Name = station.Location,
            IconKey = icon,
            High = max[0].GetDouble(),
            Low = min
        };
    }

    private static readonly (string Name, double Lat, double Lon)[] TravelCityList =
    [
        ("Atlanta", 33.749, -84.388),
        ("Boston", 42.3584, -71.0598),
        ("Chicago", 41.9796, -87.9045),
        ("Cleveland", 41.4995, -81.6954),
        ("Dallas", 32.8959, -97.0372),
        ("Denver", 39.7391, -104.9847),
        ("Detroit", 42.3314, -83.0457),
        ("Hartford", 41.7637, -72.6851),
        ("Houston", 29.7633, -95.3633),
        ("Indianapolis", 39.7684, -86.158),
        ("Los Angeles", 34.0522, -118.2437),
        ("Miami", 25.7743, -80.1937),
        ("Minneapolis", 44.98, -93.2638),
        ("New York", 40.7142, -74.0059),
        ("Norfolk", 36.8468, -76.2852),
        ("Orlando", 28.5383, -81.3792),
        ("Philadelphia", 39.9523, -75.1638),
        ("Pittsburgh", 40.4406, -79.9959),
        ("St. Louis", 38.6273, -90.1979),
        ("San Francisco", 37.7749, -122.4194),
        ("Seattle", 47.6062, -122.3321),
        ("Syracuse", 43.0481, -76.1474),
        ("Tampa", 27.9475, -82.4584),
        ("Washington DC", 38.8951, -77.0364)
    ];

    private async Task<IReadOnlyList<WeatherRegionalCity>> FetchTravelCitiesAsync(bool useMetric, CancellationToken cancellationToken)
    {
        var cities = new List<WeatherRegionalCity>(TravelCityList.Length);
        try
        {
            const int batchSize = 8;
            var unit = useMetric ? "celsius" : "fahrenheit";
            for (var start = 0; start < TravelCityList.Length; start += batchSize)
            {
                var batch = TravelCityList.Skip(start).Take(batchSize).ToArray();
                var lats = string.Join(",", batch.Select(c => c.Lat.ToString("F3", CultureInfo.InvariantCulture)));
                var lons = string.Join(",", batch.Select(c => c.Lon.ToString("F3", CultureInfo.InvariantCulture)));
                var url =
                    "https://api.open-meteo.com/v1/forecast?latitude=" + lats
                    + "&longitude=" + lons
                    + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                    + "&temperature_unit=" + unit
                    + "&forecast_days=1";
                using var doc = JsonDocument.Parse(await Client().GetStringAsync(url, cancellationToken));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var count = Math.Min(root.GetArrayLength(), batch.Length);
                    for (var i = 0; i < count; i++)
                    {
                        cities.Add(ReadTravelDaily(root[i], batch[i].Name));
                    }
                }
                else if (batch.Length > 0)
                {
                    cities.Add(ReadTravelDaily(root, batch[0].Name));
                }
            }

            return cities;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Travel city forecasts unavailable");
            return cities;
        }
    }

    private static WeatherRegionalCity ReadTravelDaily(JsonElement root, string name)
    {
        if (!root.TryGetProperty("daily", out var daily)
            || !daily.TryGetProperty("temperature_2m_max", out var max)
            || max.GetArrayLength() == 0)
        {
            return new WeatherRegionalCity { Name = name };
        }

        var min = daily.TryGetProperty("temperature_2m_min", out var minEl) && minEl.GetArrayLength() > 0
            ? minEl[0].GetDouble()
            : (double?)null;
        var icon = "Cloudy";
        if (daily.TryGetProperty("weather_code", out var codes) && codes.GetArrayLength() > 0)
        {
            icon = WeatherIconMap.FromWmo(codes[0].GetInt32());
        }

        return new WeatherRegionalCity
        {
            Name = name,
            IconKey = icon,
            High = max[0].GetDouble(),
            Low = min
        };
    }

    private async Task<IReadOnlyList<WeatherSpcDay>> FetchSpcOutlookAsync(GeoPlace place, CancellationToken cancellationToken)
    {
        try
        {
            var client = Client();
            var now = InPlaceNow(place);
            return await Task.WhenAll(
                FetchSpcDayAsync(client, 1, now, place, cancellationToken),
                FetchSpcDayAsync(client, 2, now, place, cancellationToken),
                FetchSpcDayAsync(client, 3, now, place, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SPC outlook fetch failed");
            return [];
        }
    }

    private static DateTimeOffset InPlaceNow(GeoPlace place)
    {
        if (!string.IsNullOrWhiteSpace(place.Timezone)
            && TimeZoneInfo.TryFindSystemTimeZoneById(place.Timezone, out var tz))
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        }

        return DateTimeOffset.Now;
    }

    private async Task<WeatherSpcDay> FetchSpcDayAsync(
        HttpClient client,
        int dayNumber,
        DateTimeOffset now,
        GeoPlace place,
        CancellationToken cancellationToken)
    {
        var name = now.AddDays(dayNumber - 1).ToString("dddd", CultureInfo.InvariantCulture);
        try
        {
            var url = $"https://www.spc.noaa.gov/products/outlook/day{dayNumber}otlk_cat.nolyr.geojson";
            using var doc = JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
            var bestLabel = "NONE";
            var bestText = "No Risk";
            var bestRank = -1;
            if (doc.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("geometry", out var geometry)
                        || !PointInSpcGeometry(geometry, place.Longitude, place.Latitude))
                    {
                        continue;
                    }

                    var props = feature.GetProperty("properties");
                    var label = props.TryGetProperty("LABEL", out var lbl) ? lbl.GetString() ?? "NONE" : "NONE";
                    var rank = SpcRiskRank(label);
                    if (rank <= bestRank)
                    {
                        continue;
                    }

                    bestRank = rank;
                    bestLabel = label;
                    bestText = props.TryGetProperty("LABEL2", out var l2) && !string.IsNullOrWhiteSpace(l2.GetString())
                        ? l2.GetString()!
                        : SpcRiskText(label);
                }
            }

            return new WeatherSpcDay
            {
                DayName = name,
                RiskLabel = bestLabel,
                RiskText = bestText
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SPC outlook day {Day} failed", dayNumber);
            return new WeatherSpcDay
            {
                DayName = name,
                RiskLabel = "NONE",
                RiskText = "Unavailable"
            };
        }
    }

    private static int SpcRiskRank(string label)
        => label.ToUpperInvariant() switch
        {
            "TSTM" => 0,
            "MRGL" => 1,
            "SLGT" => 2,
            "ENH" => 3,
            "MDT" => 4,
            "HIGH" => 5,
            _ => -1
        };

    private static string SpcRiskText(string label)
        => label.ToUpperInvariant() switch
        {
            "TSTM" => "Thunderstorms",
            "MRGL" => "Marginal",
            "SLGT" => "Slight",
            "ENH" => "Enhanced",
            "MDT" => "Moderate",
            "HIGH" => "High",
            _ => "No Risk"
        };

    private static bool PointInSpcGeometry(JsonElement geometry, double lon, double lat)
    {
        if (!geometry.TryGetProperty("type", out var typeEl) || !geometry.TryGetProperty("coordinates", out var coords))
        {
            return false;
        }

        var type = typeEl.GetString();
        if (string.Equals(type, "Polygon", StringComparison.OrdinalIgnoreCase) && coords.GetArrayLength() > 0)
        {
            return PointInRing(coords[0], lon, lat);
        }

        if (string.Equals(type, "MultiPolygon", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var polygon in coords.EnumerateArray())
            {
                if (polygon.GetArrayLength() > 0 && PointInRing(polygon[0], lon, lat))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PointInRing(JsonElement ring, double lon, double lat)
    {
        var inside = false;
        var count = ring.GetArrayLength();
        if (count < 3)
        {
            return false;
        }

        var j = count - 1;
        for (var i = 0; i < count; i++)
        {
            var xi = ring[i][0].GetDouble();
            var yi = ring[i][1].GetDouble();
            var xj = ring[j][0].GetDouble();
            var yj = ring[j][1].GetDouble();
            var intersect = yi > lat != yj > lat
                && lon < (xj - xi) * (lat - yi) / ((yj - yi) + double.Epsilon) + xi;
            if (intersect)
            {
                inside = !inside;
            }

            j = i;
        }

        return inside;
    }

    private async Task<StationParse?> TryParseStationAsync(
        HttpClient client,
        string stationId,
        string city,
        double? latitude,
        double? longitude,
        bool useMetric,
        CancellationToken cancellationToken)
    {
        try
        {
            using var obsDoc = JsonDocument.Parse(
                await client.GetStringAsync($"https://api.weather.gov/stations/{stationId}/observations?limit=2", cancellationToken));
            if (!obsDoc.RootElement.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
            {
                return null;
            }

            var p = features[0].GetProperty("properties");
            string? pressureDirection = null;
            if (features.GetArrayLength() > 1
                && ReadNwsPascals(p) is double latestPa
                && ReadNwsPascals(features[1].GetProperty("properties")) is double previousPa)
            {
                var diff = latestPa - previousPa;
                if (diff > 150)
                {
                    pressureDirection = "R";
                }
                else if (diff < -150)
                {
                    pressureDirection = "F";
                }
            }

            var current = ParseNwsObservation(p, city, useMetric, pressureDirection);
            if (current is null)
            {
                return null;
            }

            return new StationParse(current, ToObservationRow(current, useMetric, latitude, longitude));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NWS observation fetch failed for {Station}", stationId);
            return null;
        }
    }

    private static WeatherCurrent? ParseNwsObservation(
        JsonElement p,
        string stationName,
        bool useMetric,
        string? pressureDirection = null)
    {
        var temp = ConvertNwsTemperature(p, "temperature", useMetric);
        if (temp is null)
        {
            return null;
        }

        var dew = ConvertNwsTemperature(p, "dewpoint", useMetric);
        var wind = ConvertNwsWind(p, useMetric);
        var gust = ConvertNwsWind(p, useMetric, "windGust");
        var vis = ConvertNwsVisibility(p, useMetric);
        var pressure = ConvertNwsPressure(p, useMetric);
        var heat = ConvertNwsTemperature(p, "heatIndex", useMetric);
        var chill = ConvertNwsTemperature(p, "windChill", useMetric);
        double? feels = null;
        string? apparentLabel = null;
        if (heat is double hi && hi > temp.Value + 0.5)
        {
            feels = hi;
            apparentLabel = "Heat Index:";
        }
        else if (chill is double wc && wc < temp.Value - 0.5)
        {
            feels = wc;
            apparentLabel = "Wind Chill:";
        }

        return new WeatherCurrent
        {
            IconKey = WeatherIconMap.FromNwsIcon(
                p.TryGetProperty("icon", out var icon) ? icon.GetString() : null,
                p.TryGetProperty("textDescription", out var td) ? td.GetString() : null),
            ConditionText = p.TryGetProperty("textDescription", out var desc)
                ? desc.GetString() ?? "Current conditions"
                : "Current conditions",
            Temperature = temp.Value,
            FeelsLike = feels,
            ApparentLabel = apparentLabel,
            Dewpoint = dew,
            Humidity = GetUnitValue(p, "relativeHumidity") is double h ? (int)Math.Round(h) : null,
            WindSpeed = wind,
            WindGust = gust,
            WindDirection = GetUnitValue(p, "windDirection") is double deg ? WeatherIconMap.Cardinal(deg) : null,
            Pressure = pressure,
            PressureDirection = pressureDirection,
            Visibility = vis,
            Ceiling = ReadNwsCeiling(p, useMetric),
            StationName = stationName
        };
    }

    private static WeatherStationObservation ToObservationRow(
        WeatherCurrent current,
        bool useMetric,
        double? latitude = null,
        double? longitude = null)
    {
        string wind;
        if (current.WindSpeed is null or <= 0)
        {
            wind = "Calm";
        }
        else
        {
            var dir = string.IsNullOrWhiteSpace(current.WindDirection) ? "" : current.WindDirection + " ";
            wind = dir + Math.Round(current.WindSpeed.Value).ToString("0", CultureInfo.InvariantCulture);
            if (useMetric)
            {
                wind += "k";
            }
        }

        return new WeatherStationObservation
        {
            Location = current.StationName ?? "Station",
            Temperature = current.Temperature,
            Weather = ShortenObservationWeather(current.ConditionText),
            Wind = wind,
            IconKey = current.IconKey,
            Latitude = latitude,
            Longitude = longitude
        };
    }

    private static string ShortStationCity(string? name, string id)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return id;
        }

        var city = name.Split(',')[0].Trim();
        foreach (var strip in new[]
                 {
                     " International Airport",
                     " Regional Airport",
                     " Municipal Airport",
                     " Municipal",
                     " Airport",
                     " Weather Forecast Office",
                     " Weather Station"
                 })
        {
            if (city.EndsWith(strip, StringComparison.OrdinalIgnoreCase))
            {
                city = city[..^strip.Length].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(city) ? id : city;
    }

    private static string ShortenObservationWeather(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return "-";
        }

        return condition
            .Replace("Light ", "L ", StringComparison.OrdinalIgnoreCase)
            .Replace("Heavy ", "H ", StringComparison.OrdinalIgnoreCase)
            .Replace("Partly ", "P ", StringComparison.OrdinalIgnoreCase)
            .Replace("Mostly ", "M ", StringComparison.OrdinalIgnoreCase)
            .Replace("Thunderstorm", "T'storm", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("Freezing Rain", "Frz Rn", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private sealed record StationParse(WeatherCurrent Current, WeatherStationObservation Row);

    private async Task<IReadOnlyList<WeatherAlert>> FetchAlertsAsync(
        HttpClient client,
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        try
        {
            var lat = latitude.ToString("F4", CultureInfo.InvariantCulture);
            var lon = longitude.ToString("F4", CultureInfo.InvariantCulture);
            using var alertDoc = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"https://api.weather.gov/alerts/active?point={lat},{lon}&status=actual",
                    cancellationToken));
            var alerts = new List<WeatherAlert>();
            foreach (var feature in alertDoc.RootElement.GetProperty("features").EnumerateArray())
            {
                var ap = feature.GetProperty("properties");
                var severity = ap.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "" : "";
                if (string.Equals(severity, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var description = ap.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                alerts.Add(new WeatherAlert
                {
                    Event = ap.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "",
                    Headline = ap.TryGetProperty("headline", out var hl) ? hl.GetString() ?? "" : "",
                    Description = description,
                    Severity = severity
                });
                if (alerts.Count >= 5)
                {
                    break;
                }
            }

            return alerts
                .OrderByDescending(a => a.Severity switch
                {
                    "Extreme" => 4,
                    "Severe" => 3,
                    "Moderate" => 2,
                    "Minor" => 1,
                    _ => 0
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NWS alerts fetch failed");
            return [];
        }
    }

    private async Task<IReadOnlyList<WeatherRadarFrame>> FetchRadarAsync(
        HttpClient client,
        GeoPlace place,
        CancellationToken cancellationToken)
    {
        var frames = new List<WeatherRadarFrame>();
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5 })
        {
            try
            {
                var url = $"https://mesonet.agron.iastate.edu/data/gis/images/4326/USCOMP/n0r_{index}.png";
                var bytes = await client.GetByteArrayAsync(url, cancellationToken);
                if (bytes.Length < 100)
                {
                    continue;
                }

                frames.Add(new WeatherRadarFrame
                {
                    Time = DateTimeOffset.UtcNow.AddMinutes(-index * 5),
                    Image = WeatherStarRadar.CropReflectivity(bytes, place.Latitude, place.Longitude)
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mesonet radar frame {Index} failed", index);
            }
        }

        return frames;
    }

    private static List<WeatherHourly> ReadUpcomingHourly(JsonElement root, int maxCount)
    {
        var hourly = new List<WeatherHourly>();
        if (!root.TryGetProperty("hourly", out var hourlyEl)
            || !hourlyEl.TryGetProperty("time", out var times)
            || times.ValueKind != JsonValueKind.Array)
        {
            return hourly;
        }

        var temps = hourlyEl.GetProperty("temperature_2m");
        var codes = hourlyEl.GetProperty("weather_code");
        var pops = hourlyEl.TryGetProperty("precipitation_probability", out var pop) ? pop : default;
        var feels = hourlyEl.TryGetProperty("apparent_temperature", out var app) ? app : default;
        var winds = hourlyEl.TryGetProperty("wind_speed_10m", out var windEl) ? windEl : default;
        var windDirs = hourlyEl.TryGetProperty("wind_direction_10m", out var windDirEl) ? windDirEl : default;
        var dews = hourlyEl.TryGetProperty("dew_point_2m", out var dewEl) ? dewEl : default;
        var clouds = hourlyEl.TryGetProperty("cloud_cover", out var cloudEl) ? cloudEl : default;
        var offset = root.TryGetProperty("utc_offset_seconds", out var offEl) && offEl.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(offEl.GetInt32())
            : TimeSpan.Zero;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var raw = times[i].GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var local = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None);
            var time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
            if (time < cutoff)
            {
                continue;
            }

            hourly.Add(new WeatherHourly
            {
                Time = time,
                Temperature = temps[i].GetDouble(),
                FeelsLike = ArrayNumber(feels, i),
                Dewpoint = ArrayNumber(dews, i),
                WindSpeed = ArrayNumber(winds, i),
                WindDirection = ArrayNumber(windDirs, i) is double deg ? WeatherIconMap.Cardinal(deg) : null,
                IconKey = WeatherIconMap.FromWmo(codes[i].GetInt32()),
                ConditionText = WeatherIconMap.FromWmoText(codes[i].GetInt32()),
                PrecipitationChance = ArrayNumber(pops, i) is double p ? (int)Math.Round(p) : null,
                CloudCover = ArrayNumber(clouds, i) is double c ? (int)Math.Round(c) : null
            });
            if (hourly.Count >= maxCount)
            {
                break;
            }
        }

        return hourly;
    }

    private static double? ArrayNumber(JsonElement el, int index)
    {
        if (el.ValueKind != JsonValueKind.Array || index >= el.GetArrayLength() || el[index].ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return el[index].GetDouble();
    }

    private static double ApparentTemperature(double temp, int? humidity, double? windSpeed, bool useMetric)
    {
        var fahrenheit = useMetric ? temp * 9 / 5 + 32 : temp;
        var windMph = windSpeed is null ? 0 : useMetric ? windSpeed.Value / 1.60934 : windSpeed.Value;
        double apparentF;
        if (fahrenheit >= 80 && humidity is int rh)
        {
            apparentF = -42.379
                + (2.04901523 * fahrenheit)
                + (10.14333127 * rh)
                - (0.22475541 * fahrenheit * rh)
                - (6.83783e-3 * fahrenheit * fahrenheit)
                - (5.481717e-2 * rh * rh)
                + (1.22874e-3 * fahrenheit * fahrenheit * rh)
                + (8.5282e-4 * fahrenheit * rh * rh)
                - (1.99e-6 * fahrenheit * fahrenheit * rh * rh);
        }
        else if (fahrenheit <= 50 && windMph >= 3)
        {
            var v = Math.Pow(windMph, 0.16);
            apparentF = 35.74 + (0.6215 * fahrenheit) - (35.75 * v) + (0.4275 * fahrenheit * v);
        }
        else
        {
            return temp;
        }

        return useMetric ? (apparentF - 32) * 5 / 9 : apparentF;
    }

    private static (double? Speed, string? Direction) ReadNwsHourlyWind(JsonElement period, bool useMetric)
    {
        double? speed = null;
        if (period.TryGetProperty("windSpeed", out var ws))
        {
            speed = ws.ValueKind == JsonValueKind.String
                ? ParseNwsWindPhrase(ws.GetString(), useMetric)
                : ConvertNwsWind(period, useMetric);
        }

        string? direction = null;
        if (period.TryGetProperty("windDirection", out var wd))
        {
            if (wd.ValueKind == JsonValueKind.String)
            {
                direction = wd.GetString();
            }
            else if (GetUnitValue(period, "windDirection") is double deg)
            {
                direction = WeatherIconMap.Cardinal(deg);
            }
        }

        return (speed, string.IsNullOrWhiteSpace(direction) ? null : direction);
    }

    private static double? ParseNwsWindPhrase(string? text, bool useMetric)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains("calm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\d+");
        if (matches.Count == 0)
        {
            return null;
        }

        var mph = double.Parse(matches[^1].Value, CultureInfo.InvariantCulture);
        return useMetric ? mph * 1.60934 : mph;
    }

    private static double? GetDouble(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? GetUnitValue(JsonElement props, string name)
        => TryReadNwsMeasure(props, name, out var raw, out _) ? raw : null;

    private static double? ConvertNwsTemperature(JsonElement props, string name, bool useMetric)
    {
        if (!TryReadNwsMeasure(props, name, out var raw, out var unit))
        {
            return null;
        }

        var celsius = unit.Contains("degF", StringComparison.OrdinalIgnoreCase) || unit.Contains("fahrenheit", StringComparison.OrdinalIgnoreCase)
            ? (raw - 32) * 5 / 9
            : raw;
        return useMetric ? celsius : celsius * 9 / 5 + 32;
    }

    private static double? ConvertNwsWind(JsonElement props, bool useMetric, string name = "windSpeed")
    {
        if (!TryReadNwsMeasure(props, name, out var raw, out var unit))
        {
            return null;
        }

        var kmh = ToKilometersPerHour(raw, unit);
        return useMetric ? kmh : kmh / 1.60934;
    }

    private static double? ReadNwsCeiling(JsonElement props, bool useMetric)
    {
        if (!props.TryGetProperty("cloudLayers", out var layers)
            || layers.ValueKind != JsonValueKind.Array
            || layers.GetArrayLength() == 0
            || !TryReadNwsMeasure(layers[0], "base", out var raw, out var unit)
            || raw <= 0)
        {
            return 0;
        }

        var meters = unit.Contains("ft", StringComparison.OrdinalIgnoreCase) ? raw * 0.3048 : raw;
        if (useMetric)
        {
            return Math.Round(meters);
        }

        return Math.Round(meters / 0.3048 / 100.0) * 100;
    }

    private static double? ReadNwsPascals(JsonElement props)
    {
        if (!TryReadNwsMeasure(props, "barometricPressure", out var raw, out var unit)
            && !TryReadNwsMeasure(props, "seaLevelPressure", out raw, out unit))
        {
            return null;
        }

        return unit.Contains("Pa", StringComparison.Ordinal) || string.IsNullOrEmpty(unit)
            ? raw
            : raw * 100;
    }

    private static double? ConvertNwsPressure(JsonElement props, bool useMetric)
    {
        if (!TryReadNwsMeasure(props, "barometricPressure", out var raw, out var unit)
            && !TryReadNwsMeasure(props, "seaLevelPressure", out raw, out unit))
        {
            return null;
        }

        var pascals = unit.Contains("Pa", StringComparison.Ordinal) || string.IsNullOrEmpty(unit)
            ? raw
            : raw * 100;
        return useMetric ? pascals / 100 : pascals / 3386.39;
    }

    private static double? ConvertNwsVisibility(JsonElement props, bool useMetric)
    {
        if (!TryReadNwsMeasure(props, "visibility", out var raw, out _))
        {
            return null;
        }

        return useMetric ? raw / 1000 : raw / 1609.34;
    }

    private static double ToKilometersPerHour(double raw, string unit)
    {
        if (unit.Contains("km_h", StringComparison.OrdinalIgnoreCase) || unit.Contains("km/h", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        if (unit.Contains("mi_h", StringComparison.OrdinalIgnoreCase) || unit.Contains("mph", StringComparison.OrdinalIgnoreCase))
        {
            return raw * 1.60934;
        }

        if (unit.Contains("m_s", StringComparison.OrdinalIgnoreCase) || unit.Contains("m/s", StringComparison.OrdinalIgnoreCase))
        {
            return raw * 3.6;
        }

        // NWS currently reports km/h; older payloads used m/s.
        return raw;
    }

    private static bool TryReadNwsMeasure(JsonElement props, string name, out double value, out string unit)
    {
        value = 0;
        unit = "";
        if (!props.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!node.TryGetProperty("value", out var raw) || raw.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = raw.GetDouble();
        unit = node.TryGetProperty("unitCode", out var unitEl) ? unitEl.GetString() ?? "" : "";
        return true;
    }
}
