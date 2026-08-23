using System.Globalization;
using FinTv.Services;
using SkiaSharp;

namespace FinTv.Weather;

public sealed class WeatherStarCompositor
{
    private readonly WeatherStarAssets _assets;

    public WeatherStarCompositor(WeatherStarAssets assets)
    {
        _assets = assets;
    }

    public byte[] RenderJpeg(
        WeatherSnapshot snap,
        WeatherStarScreen screen,
        WeatherStarDockerVariant skin,
        int width,
        int height,
        bool scanlines,
        int radarIndex,
        int screenRepeat = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(skin == WeatherStarDockerVariant.Ws3kp ? new SKColor(0x10, 0x20, 0x70) : new SKColor(0x00, 0x28, 0x8A));

        var wide = width > 700;
        var bg = _assets.Background(skin, wide, screen);
        if (bg is not null)
        {
            DrawBitmap(canvas, bg, new SKRect(0, 0, width, height));
        }

        var font = _assets.Font(skin);
        var large = _assets.Font(skin, large: true);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var yellow = new SKPaint { Color = new SKColor(0xFF, 0xE1, 0x4A), IsAntialias = true };

        if (screen == WeatherStarScreen.LocalForecast)
        {
            DrawLocalForecastChrome(canvas, snap, font, width, white, yellow);
        }
        else if (screen != WeatherStarScreen.Hazards)
        {
            var originX = ContentOriginX(width);
            DrawText(canvas, snap.Place.DisplayName.ToUpperInvariant(), font, 16, originX + 10, BaselineBelow(font, 16, 8), yellow);
            DrawText(canvas, PlaceNow(snap).ToString("h:mm tt", CultureInfo.InvariantCulture), font, 16, originX + 585, BaselineBelow(font, 16, 8), white, SKTextAlign.Right);
            DrawText(canvas, Title(screen), font, 24, originX + 170, BaselineBelow(font, 24, 36), white);
        }

        switch (screen)
        {
            case WeatherStarScreen.Current:
                DrawCurrent(canvas, snap, font, large, width, white, yellow);
                break;
            case WeatherStarScreen.Observations:
                DrawObservations(canvas, snap, font, width, white, yellow);
                break;
            case WeatherStarScreen.Hourly:
                DrawHourly(canvas, snap, font, large, width, height, radarIndex, white, yellow);
                break;
            case WeatherStarScreen.HourlyGraph:
                DrawHourlyGraph(canvas, snap, font, width, height, white, yellow);
                break;
            case WeatherStarScreen.LocalForecast:
                DrawLocalForecast(canvas, snap, font, width, height, radarIndex, screenRepeat, white, yellow);
                break;
            case WeatherStarScreen.ExtendedForecast:
                DrawForecast(canvas, snap, font, 6, width, white, yellow);
                break;
            case WeatherStarScreen.Regional:
                DrawRegional(canvas, snap, font, width, height, white, yellow);
                break;
            case WeatherStarScreen.Hazards:
                DrawHazards(canvas, snap, font, width, height, radarIndex, white, yellow);
                break;
            case WeatherStarScreen.Radar:
                DrawRadar(canvas, snap, font, width, height, radarIndex, white);
                break;
            case WeatherStarScreen.Almanac:
                DrawForecast(canvas, snap, font, 4, width, white, yellow);
                break;
            case WeatherStarScreen.SpcOutlook:
                DrawSpcOutlook(canvas, snap, font, width, white, yellow);
                break;
            case WeatherStarScreen.Travel:
                DrawTravel(canvas, snap, font, width, radarIndex, white, yellow);
                break;
        }

        if (snap.Alerts.Count > 0 && screen != WeatherStarScreen.Hazards)
        {
            DrawText(canvas, snap.Alerts[0].Event.ToUpperInvariant(), font, 14, 10, height - 18, yellow);
        }

        if (scanlines)
        {
            using var line = new SKPaint { Color = new SKColor(0, 0, 0, 50) };
            for (var y = 0; y < height; y += 2)
            {
                canvas.DrawRect(0, y, width, 1, line);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        return data.ToArray();
    }

    private void DrawCurrent(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, SKTypeface large, int width, SKPaint white, SKPaint yellow)
    {
        var cur = snap.Current;
        if (cur is null)
        {
            DrawText(canvas, "NO CURRENT DATA", font, 22, ContentOriginX(width) + 80, 160, white);
            return;
        }

        var unit = snap.UseMetric ? "C" : "F";
        var ox = ContentOriginX(width);
        const float boxMargin = 64f;
        const float colW = 255f;
        var leftCol = ox + boxMargin;
        var rightCol = leftCol + colW;
        var leftCenter = leftCol + colW / 2f;
        var rightCenter = rightCol + colW / 2f;
        var rightLabelX = rightCol + 20f;
        var rightValueX = rightCol + colW - 12f;
        var windLeft = leftCol + 12f;
        var windRight = leftCol + colW - 12f;

        var condition = string.IsNullOrWhiteSpace(cur.ConditionText) ? "-" : cur.ConditionText.Trim();
        if (condition.Length > 15)
        {
            condition = ShortenWeather(condition);
        }

        DrawText(
            canvas,
            Math.Round(cur.Temperature).ToString("0", CultureInfo.InvariantCulture) + "°",
            large,
            32,
            leftCenter,
            118,
            white,
            SKTextAlign.Center);
        DrawText(canvas, condition, font, 20, leftCenter, 150, yellow, SKTextAlign.Center);

        var icon = _assets.Icon(cur.IconKey);
        if (icon is not null)
        {
            const float iconSize = 108f;
            var iconX = leftCenter - iconSize / 2f;
            DrawBitmap(canvas, icon, new SKRect(iconX, 164, iconX + iconSize, 164 + iconSize));
        }

        DrawText(canvas, "Wind:", font, 20, windLeft, 318, white);
        DrawText(canvas, FormatCurrentWind(cur), font, 20, windRight, 318, white, SKTextAlign.Right);
        if (cur.WindGust is double gust && gust > (cur.WindSpeed ?? 0) + 0.5)
        {
            DrawText(
                canvas,
                "Gusts to " + Math.Round(gust).ToString("0", CultureInfo.InvariantCulture),
                font,
                20,
                windRight,
                346,
                white,
                SKTextAlign.Right);
        }

        DrawText(
            canvas,
            Truncate(cur.StationName ?? snap.Place.DisplayName, 16).ToUpperInvariant(),
            font,
            18,
            rightCenter,
            118,
            yellow,
            SKTextAlign.Center);

        var y = 154f;
        DrawCurrentRow(canvas, font, "Humidity:", cur.Humidity is int humidity ? humidity + "%" : "-", rightLabelX, rightValueX, y, white);
        y += 36;
        DrawCurrentRow(
            canvas,
            font,
            "Dewpoint:",
            cur.Dewpoint is double dew ? Math.Round(dew).ToString("0", CultureInfo.InvariantCulture) + "°" : "-",
            rightLabelX,
            rightValueX,
            y,
            white);
        y += 36;
        DrawCurrentRow(canvas, font, "Ceiling:", FormatCeiling(cur.Ceiling, snap.UseMetric), rightLabelX, rightValueX, y, white);
        y += 36;
        DrawCurrentRow(canvas, font, "Visibility:", FormatVisibility(cur.Visibility, snap.UseMetric), rightLabelX, rightValueX, y, white);
        y += 36;
        if (cur.Pressure is double pressure)
        {
            var pressureText = snap.UseMetric
                ? Math.Round(pressure).ToString("0", CultureInfo.InvariantCulture) + " mb"
                : pressure.ToString("0.00", CultureInfo.InvariantCulture) + " in";
            if (!string.IsNullOrWhiteSpace(cur.PressureDirection))
            {
                pressureText += " " + cur.PressureDirection;
            }

            DrawCurrentRow(canvas, font, "Pressure:", pressureText, rightLabelX, rightValueX, y, white);
            y += 36;
        }

        if (!string.IsNullOrWhiteSpace(cur.ApparentLabel) && cur.FeelsLike is double feels)
        {
            DrawCurrentRow(
                canvas,
                font,
                cur.ApparentLabel.TrimEnd(':') + ":",
                Math.Round(feels).ToString("0", CultureInfo.InvariantCulture) + "°" + unit,
                rightLabelX,
                rightValueX,
                y,
                white);
        }
    }

    private static void DrawCurrentRow(
        SKCanvas canvas,
        SKTypeface font,
        string label,
        string value,
        float labelX,
        float valueX,
        float y,
        SKPaint color)
    {
        DrawText(canvas, label, font, 18, labelX, y, color);
        DrawText(canvas, value, font, 18, valueX, y, color, SKTextAlign.Right);
    }

    private static string FormatCurrentWind(WeatherCurrent cur)
    {
        if (cur.WindSpeed is null or <= 0)
        {
            return "Calm";
        }

        var dir = string.IsNullOrWhiteSpace(cur.WindDirection) ? "" : cur.WindDirection.Trim().PadRight(3);
        var speed = Math.Round(cur.WindSpeed.Value).ToString("0", CultureInfo.InvariantCulture);
        return (dir + " " + speed).Trim();
    }

    private static string FormatCeiling(double? ceiling, bool metric)
    {
        if (ceiling is null or <= 0)
        {
            return "Unlimited";
        }

        return Math.Round(ceiling.Value).ToString("0", CultureInfo.InvariantCulture) + (metric ? " m." : " ft.");
    }

    private static string FormatVisibility(double? visibility, bool metric)
    {
        if (visibility is null)
        {
            return "-";
        }

        return Math.Round(visibility.Value).ToString("0", CultureInfo.InvariantCulture) + (metric ? " km." : " mi.");
    }

    private static void DrawObservations(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, SKPaint white, SKPaint yellow)
    {
        var rows = snap.Observations;
        if (rows.Count == 0 && snap.Current is { } cur)
        {
            rows =
            [
                new WeatherStationObservation
                {
                    Location = Truncate(cur.StationName ?? snap.Place.DisplayName, 14),
                    Temperature = cur.Temperature,
                    Weather = ShortenWeather(cur.ConditionText),
                    Wind = FormatWind(cur.WindDirection, cur.WindSpeed, snap.UseMetric)
                }
            ];
        }

        // has-box main starts at x=64 on the 640 canvas (latest-observations.scss).
        var ox = ContentOriginX(width);
        var locX = ox + 72f;
        var tempX = ox + 294f;
        var weatherX = ox + 344f;
        var windRight = ox + 568f;
        var y = 92f;
        DrawText(canvas, "LOCATION", font, 16, locX, y, yellow);
        DrawText(canvas, snap.UseMetric ? "°C" : "°F", font, 16, tempX, y, yellow);
        DrawText(canvas, "WEATHER", font, 16, weatherX, y, yellow);
        DrawText(canvas, "WIND", font, 16, windRight, y, yellow, SKTextAlign.Right);

        if (rows.Count == 0)
        {
            DrawText(canvas, "NO STATION DATA", font, 22, locX, 180, white);
            return;
        }

        y = 128f;
        foreach (var row in rows.Take(7))
        {
            DrawText(canvas, ObservationLocation(row.Location), font, 18, locX, y, yellow);
            DrawText(canvas, Math.Round(row.Temperature).ToString("0", CultureInfo.InvariantCulture), font, 18, tempX, y, white);
            DrawText(canvas, Truncate(row.Weather, 9), font, 18, weatherX, y, white);
            DrawText(canvas, row.Wind, font, 18, windRight, y, white, SKTextAlign.Right);
            y += 36;
        }
    }

    private static string FormatWind(string? direction, double? speed, bool metric)
    {
        if (speed is null || speed <= 0)
        {
            return "Calm";
        }

        var dir = string.IsNullOrWhiteSpace(direction) ? "" : direction + " ";
        return dir + Math.Round(speed.Value).ToString("0", CultureInfo.InvariantCulture) + (metric ? " km/h" : "");
    }

    private static string ShortenWeather(string? condition)
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
            .Replace("Few ", "F ", StringComparison.OrdinalIgnoreCase)
            .Replace("Thunderstorm", "T'storm", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", " ", StringComparison.OrdinalIgnoreCase)
            .Replace(" with ", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("Freezing Rain", "Frz Rn", StringComparison.OrdinalIgnoreCase)
            .Replace("Freezing", "Frz", StringComparison.OrdinalIgnoreCase)
            .Replace("Vicinity", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd();

    private static string ObservationLocation(string? name)
    {
        var text = (name ?? "").Split(',')[0].Trim();
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
            if (text.EndsWith(strip, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^strip.Length].Trim();
            }
        }

        if (text.Length == 0)
        {
            text = name ?? "";
        }

        return Truncate(text.ToUpperInvariant(), 14);
    }

    private void DrawHourly(
        SKCanvas canvas,
        WeatherSnapshot snap,
        SKTypeface font,
        SKTypeface large,
        int width,
        int height,
        int radarIndex,
        SKPaint white,
        SKPaint gold)
    {
        var hours = UpcomingHours(snap);
        var originX = ContentOriginX(width);
        if (hours.Count == 0)
        {
            DrawText(canvas, "NO HOURLY DATA", font, 22, originX + 80, 160, white);
            return;
        }

        const int pageSize = 4;
        var maxOffset = Math.Max(0, hours.Count - pageSize);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 20);
        var page = hours.Skip(offset).Take(pageSize).ToList();

        // hourly.scss left positions on the 640 canvas; keep every column inside the 1.png box (x≈52–583).
        var hourX = originX + 70f;
        var iconX = originX + 248f;
        var tempX = originX + 355f;
        var likeX = originX + 430f;
        var windRight = originX + 568f;
        const float iconSize = 64f;

        using var headerBar = new SKPaint { Color = new SKColor(32, 0, 87) };
        using var heat = new SKPaint { Color = new SKColor(0xEE, 0x00, 0x00), IsAntialias = true };
        using var chill = new SKPaint { Color = new SKColor(0x80, 0x80, 0xFF), IsAntialias = true };
        canvas.DrawRect(originX + 52, 90, 532, 20, headerBar);
        DrawText(canvas, "TEMP", font, 18, tempX, 107, gold);
        DrawText(canvas, "LIKE", font, 18, likeX, 107, gold);
        DrawText(canvas, "WIND", font, 18, windRight, 107, gold, SKTextAlign.Right);

        var y = 150f;
        const float row = 72f;
        foreach (var hour in page)
        {
            var local = InPlace(hour.Time, snap);
            DrawText(canvas, local.ToString("ddd h tt", CultureInfo.InvariantCulture), large, 22, hourX, y, gold);
            var icon = _assets.Icon(hour.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(iconX, y - 46, iconX + iconSize, y + 18));
            }

            DrawText(
                canvas,
                Math.Round(hour.Temperature).ToString("0", CultureInfo.InvariantCulture),
                large,
                24,
                tempX,
                y,
                white);
            var feels = hour.FeelsLike ?? hour.Temperature;
            var likePaint = feels < hour.Temperature - 0.5 ? chill
                : feels > hour.Temperature + 0.5 ? heat
                : white;
            DrawText(
                canvas,
                Math.Round(feels).ToString("0", CultureInfo.InvariantCulture),
                large,
                24,
                likeX,
                y,
                likePaint);
            DrawText(canvas, FormatHourlyWind(hour), large, 22, windRight, y, white, SKTextAlign.Right);
            y += row;
            if (y > height - 36)
            {
                break;
            }
        }
    }

    private static void DrawHourlyGraph(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, SKPaint white, SKPaint gold)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
        var wide = width > 700;
        var hourCount = wide ? 48 : 36;
        var hours = snap.Hourly.Where(hour => hour.Time >= cutoff).Take(hourCount).ToList();
        if (hours.Count < 2)
        {
            DrawText(canvas, "NO HOURLY DATA", font, 22, ContentOriginX(width) + 80, 160, white);
            return;
        }

        using var tempPaint = new SKPaint { Color = new SKColor(0xFF, 0x20, 0x20), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var dewPaint = new SKPaint { Color = new SKColor(0x00, 0xC0, 0x00), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var precipPaint = new SKPaint { Color = new SKColor(0x00, 0xFF, 0xFF), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var cloudPaint = new SKPaint { Color = new SKColor(0xD0, 0xD0, 0xD0), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var tempFill = new SKPaint { Color = tempPaint.Color, IsAntialias = true };
        using var dewFill = new SKPaint { Color = dewPaint.Color, IsAntialias = true };
        using var precipFill = new SKPaint { Color = precipPaint.Color, IsAntialias = true };
        using var grid = new SKPaint { Color = new SKColor(255, 255, 255, 40), StrokeWidth = 1, Style = SKPaintStyle.Stroke };

        // 1-chart.png box is x≈52–583; 1-chart-wide.png is x≈52–797. Chart is 532×285 (746 wide).
        var chartX = 50f;
        var chartY = 95f;
        var chartW = wide ? 746f : 532f;
        var chartH = 285f;
        var plotTop = chartY + 10f;
        var plotBottom = chartY + chartH - 10f;
        var ox = ContentOriginX(width);
        DrawText(canvas, "Temp", font, 14, ox + 400, 58, tempFill);
        DrawText(canvas, "Dew", font, 14, ox + 470, 58, dewFill);
        DrawText(canvas, "Precip%", font, 14, ox + 580, 58, precipFill, SKTextAlign.Right);

        var temps = hours.Select(h => h.Temperature).ToList();
        var dews = hours.Select(h => h.Dewpoint ?? h.Temperature).ToList();
        var min = temps.Concat(dews).Min();
        var max = temps.Concat(dews).Max();
        if (Math.Abs(max - min) < 1)
        {
            max = min + 10;
        }

        var third = (max - min) / 3;
        var yLabels = new[] { max, min + third * 2, min + third, min };
        float YTemp(double v) => plotBottom - (float)((v - min) / (max - min) * (plotBottom - plotTop));
        float YPct(int? v) => plotBottom - (v.GetValueOrDefault() / 100f * (plotBottom - plotTop));
        float XAt(int i) => chartX + chartW * i / Math.Max(1, hours.Count - 1);

        for (var i = 0; i < yLabels.Length; i++)
        {
            var y = YTemp(yLabels[i]);
            canvas.DrawLine(chartX, y, chartX + chartW, y, grid);
            DrawText(
                canvas,
                Math.Round(yLabels[i]).ToString("0", CultureInfo.InvariantCulture) + "°",
                font,
                14,
                chartX - 6,
                y + 5,
                gold,
                SKTextAlign.Right);
        }

        canvas.Save();
        canvas.ClipRect(new SKRect(chartX, chartY, chartX + chartW, chartY + chartH));
        if (hours.Any(h => h.CloudCover.HasValue))
        {
            DrawPolyline(canvas, hours.Count, XAt, i => YPct(hours[i].CloudCover), cloudPaint);
        }

        if (hours.Any(h => h.PrecipitationChance.HasValue))
        {
            DrawPolyline(canvas, hours.Count, XAt, i => YPct(hours[i].PrecipitationChance), precipPaint);
        }

        DrawPolyline(canvas, hours.Count, XAt, i => YTemp(dews[i]), dewPaint);
        DrawPolyline(canvas, hours.Count, XAt, i => YTemp(temps[i]), tempPaint);
        canvas.Restore();

        var xTicks = wide ? 6 : 4;
        DateTimeOffset? prev = null;
        for (var t = 0; t <= xTicks; t++)
        {
            var i = (int)Math.Round(t * (hours.Count - 1) / (double)xTicks);
            i = Math.Clamp(i, 0, hours.Count - 1);
            var local = InPlace(hours[i].Time, snap);
            var label = GraphHourLabel(local, prev);
            prev = local;
            DrawText(canvas, label, font, 14, XAt(i), chartY + chartH + 18, gold, SKTextAlign.Center);
        }
    }

    private static string GraphHourLabel(DateTimeOffset time, DateTimeOffset? previous)
    {
        var hour = time.ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant();
        if (hour.EndsWith('m'))
        {
            hour = hour[..^1];
        }

        if (previous is DateTimeOffset prior && prior.DayOfWeek != time.DayOfWeek)
        {
            return time.ToString("ddd", CultureInfo.InvariantCulture) + " " + hour;
        }

        return hour;
    }

    private static void DrawPolyline(SKCanvas canvas, int count, Func<int, float> xAt, Func<int, float> yAt, SKPaint paint)
    {
        var builder = new SKPathBuilder();
        builder.MoveTo(xAt(0), yAt(0));
        for (var i = 1; i < count; i++)
        {
            builder.LineTo(xAt(i), yAt(i));
        }

        using var path = builder.Detach();
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 160),
            IsAntialias = true,
            StrokeWidth = paint.StrokeWidth + 2,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawPath(path, shadow);
        canvas.DrawPath(path, paint);
    }

    private static List<WeatherHourly> UpcomingHours(WeatherSnapshot snap)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-20);
        return snap.Hourly.Where(hour => hour.Time >= cutoff).Take(24).ToList();
    }

    private static string FormatHourlyWind(WeatherHourly hour)
    {
        if (hour.WindSpeed is null or <= 0)
        {
            return "Calm";
        }

        var dir = string.IsNullOrWhiteSpace(hour.WindDirection) ? "" : hour.WindDirection.Trim();
        var speed = Math.Round(hour.WindSpeed.Value).ToString("0", CultureInfo.InvariantCulture);
        var pad = Math.Max(1, 6 - dir.Length - speed.Length);
        return dir + new string(' ', pad) + speed;
    }

    private static float ContentOriginX(int width)
        => width > 700 ? (width - 640f) / 2f : 0f;

    private static DateTimeOffset PlaceNow(WeatherSnapshot snap) => InPlace(DateTimeOffset.UtcNow, snap);

    private static DateTimeOffset InPlace(DateTimeOffset time, WeatherSnapshot snap)
    {
        if (!string.IsNullOrWhiteSpace(snap.Place.Timezone)
            && TimeZoneInfo.TryFindSystemTimeZoneById(snap.Place.Timezone, out var tz))
        {
            return TimeZoneInfo.ConvertTime(time, tz);
        }

        return time;
    }

    private void DrawForecast(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int count, int width, SKPaint white, SKPaint gold)
    {
        var days = snap.Daily.Take(count).ToList();
        if (days.Count == 0 && snap.Periods.Count > 0)
        {
            days = snap.Periods.Take(count).Select(period => new WeatherDaily
            {
                Name = period.Name,
                Narrative = period.Narrative,
                IconKey = period.IconKey,
                High = period.IsDaytime ? period.Temperature : null,
                Low = period.IsDaytime ? null : period.Temperature
            }).ToList();
        }

        if (days.Count == 0)
        {
            DrawText(canvas, "NO FORECAST DATA", font, 22, 72, 180, white);
            return;
        }

        var boxed = count <= 4;
        var ox = boxed ? ContentOriginX(width) : 0f;
        var left = ox + (boxed ? 72f : 40f);
        var iconRight = boxed ? ox + 548f : width - 24f;
        var tempRight = iconRight - 48f;
        var wrapWidth = Math.Max(180f, tempRight - left - 12f);
        var top = boxed ? 126f : 132f;
        var bottom = boxed ? 350f : 440f;
        var rowHeight = (bottom - top) / days.Count;

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var y = top + i * rowHeight;
            DrawText(canvas, day.Name.ToUpperInvariant(), font, 16, left, y, gold);
            var temps = FormatDayTemps(day);
            if (temps.Length > 0)
            {
                DrawText(canvas, temps, font, 18, tempRight, y, white, SKTextAlign.Right);
            }

            var icon = _assets.Icon(day.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(iconRight - 40, y - 18, iconRight, y + 22));
            }

            var lines = WrapText(day.Narrative, font, 14, wrapWidth);
            if (lines.Count > 2)
            {
                lines[1] = TrimToWidth(lines[1], font, 14, wrapWidth - 18) + "...";
                lines = lines.Take(2).ToList();
            }

            var textY = y + 22;
            foreach (var line in lines)
            {
                DrawText(canvas, line, font, 14, left, textY, white);
                textY += 18;
            }
        }
    }

    private static string TrimToWidth(string text, SKTypeface typeface, float size, float maxWidth)
    {
        using var font = new SKFont(typeface, size);
        using var paint = new SKPaint { IsAntialias = true };
        if (TextWidth(font, text, paint) <= maxWidth)
        {
            return text;
        }

        while (text.Length > 1 && TextWidth(font, text + "...", paint) > maxWidth)
        {
            text = text[..^1].TrimEnd();
        }

        return text;
    }

    private void DrawRegional(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, SKPaint white, SKPaint gold)
    {
        var cities = snap.Regional;
        if (cities.Count == 0)
        {
            cities = snap.Observations.Select(row => new WeatherRegionalCity
            {
                Name = row.Location,
                IconKey = row.IconKey,
                High = row.Temperature
            }).ToList();
        }

        if (cities.Count == 0 && snap.Daily.Count > 0)
        {
            var today = snap.Daily[0];
            cities =
            [
                new WeatherRegionalCity
                {
                    Name = snap.Place.DisplayName,
                    IconKey = today.IconKey,
                    High = today.High,
                    Low = today.Low
                }
            ];
        }

        if (cities.Count == 0)
        {
            DrawText(canvas, "NO REGIONAL DATA", font, 22, 24, 180, white);
            return;
        }

        var columns = 3;
        var colWidth = (width - 48f) / columns;
        var rows = (int)Math.Ceiling(Math.Min(6, cities.Count) / (double)columns);
        var rowHeight = Math.Min(150f, (height - 120f) / Math.Max(1, rows));
        for (var i = 0; i < Math.Min(6, cities.Count); i++)
        {
            var city = cities[i];
            var col = i % columns;
            var row = i / columns;
            var x = 24f + col * colWidth;
            var y = 100f + row * rowHeight;
            DrawText(canvas, Truncate(city.Name, 14).ToUpperInvariant(), font, 16, x, y, gold);
            var icon = _assets.Icon(city.IconKey);
            if (icon is not null)
            {
                DrawBitmap(canvas, icon, new SKRect(x + 36, y + 8, x + 108, y + 80));
            }

            var temps = FormatRegionalTemps(city);
            DrawText(canvas, temps, font, 22, x + 20, y + 102, white);
        }
    }

    private static string FormatRegionalTemps(WeatherRegionalCity city)
    {
        if (city.High is double high && city.Low is double low)
        {
            return Math.Round(high).ToString("0", CultureInfo.InvariantCulture)
                + "/"
                + Math.Round(low).ToString("0", CultureInfo.InvariantCulture);
        }

        if (city.High is double hi)
        {
            return Math.Round(hi).ToString("0", CultureInfo.InvariantCulture);
        }

        if (city.Low is double lo)
        {
            return Math.Round(lo).ToString("0", CultureInfo.InvariantCulture);
        }

        return "";
    }

    private void DrawTravel(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int radarIndex, SKPaint white, SKPaint gold)
    {
        var cities = snap.Travel;
        if (cities.Count == 0)
        {
            DrawText(canvas, "NO TRAVEL DATA", font, 22, 24, 180, white);
            return;
        }

        var boxRight = width > 700 ? 770f : 555f;
        var cityX = 80f;
        var iconX = width > 700 ? 360f : 318f;
        var lowRight = boxRight - 88f;
        var highRight = boxRight - 28f;
        const float iconSize = 40f;
        const int pageSize = 4;
        const float headerY = 112f;
        const float firstRow = 148f;
        const float lastRow = 348f;
        var rowHeight = (lastRow - firstRow) / (pageSize - 1);

        using var headerBar = new SKPaint { Color = new SKColor(32, 0, 87) };
        canvas.DrawRect(52, 90, boxRight - 52, 22, headerBar);
        DrawText(canvas, "LOW", font, 16, lowRight, headerY, gold, SKTextAlign.Right);
        DrawText(canvas, "HIGH", font, 16, highRight, headerY, gold, SKTextAlign.Right);

        var maxOffset = Math.Max(0, cities.Count - pageSize);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 16);
        var y = firstRow;
        foreach (var city in cities.Skip(offset).Take(pageSize))
        {
            DrawText(canvas, Truncate(city.Name, 16).ToUpperInvariant(), font, 18, cityX, y, gold);
            var icon = _assets.Icon(city.IconKey);
            if (icon is not null)
            {
                var iconTop = y - iconSize + 8;
                DrawBitmap(canvas, icon, new SKRect(iconX, iconTop, iconX + iconSize, iconTop + iconSize));
            }

            if (city.Low is double low)
            {
                DrawText(canvas, Math.Round(low).ToString("0", CultureInfo.InvariantCulture), font, 20, lowRight, y, white, SKTextAlign.Right);
            }

            if (city.High is double high)
            {
                DrawText(canvas, Math.Round(high).ToString("0", CultureInfo.InvariantCulture), font, 20, highRight, y, white, SKTextAlign.Right);
            }

            y += rowHeight;
        }
    }

    private static void DrawSpcOutlook(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, SKPaint white, SKPaint gold)
    {
        if (!snap.IsUnitedStates)
        {
            DrawText(canvas, "U.S. CONVECTIVE OUTLOOK", font, 22, 24, 160, gold);
            DrawText(canvas, "Not available outside the United States.", font, 16, 24, 200, white);
            return;
        }

        var days = snap.SpcOutlook;
        if (days.Count == 0)
        {
            DrawText(canvas, "OUTLOOK UNAVAILABLE", font, 22, 24, 180, white);
            return;
        }

        DrawText(canvas, "CATEGORICAL OUTLOOK", font, 16, width / 2f, 102, gold, SKTextAlign.Center);
        const float nameRight = 196f;
        const float barLeft = 210f;
        const float rowHeight = 72f;
        var y = 118f;
        foreach (var day in days.Take(3))
        {
            DrawText(canvas, day.DayName.ToUpperInvariant(), font, 18, nameRight, y + 38, gold, SKTextAlign.Right);
            var (barWidth, color) = SpcBar(day.RiskLabel);
            if (barWidth > 0)
            {
                var barTop = y + 16;
                var barRect = new SKRect(barLeft, barTop, barLeft + barWidth, barTop + 40);
                using var fill = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawRect(barRect, fill);
                DrawOutsetBorder(canvas, barRect);
                var label = SpcBarLabel(day.RiskLabel);
                if (label.Length > 0)
                {
                    using var measureFont = new SKFont(font, 14);
                    using var measurePaint = new SKPaint { IsAntialias = true };
                    if (TextWidth(measureFont, label, measurePaint) + 16 <= barWidth)
                    {
                        DrawText(
                            canvas,
                            label,
                            font,
                            14,
                            barLeft + 10,
                            BaselineBelow(font, 14, barTop + 10),
                            white);
                    }
                }
            }
            else
            {
                DrawText(canvas, "NO RISK", font, 16, barLeft + 8, y + 38, white);
            }

            y += rowHeight;
        }
    }

    private static void DrawOutsetBorder(SKCanvas canvas, SKRect rect)
    {
        using var highlight = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), IsAntialias = false, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var shadow = new SKPaint { Color = new SKColor(0x50, 0x50, 0x50), IsAntialias = false, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, highlight);
        canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, highlight);
        canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, shadow);
        canvas.DrawLine(rect.Right, rect.Top, rect.Right, rect.Bottom, shadow);
    }

    private static (float Width, SKColor Color) SpcBar(string label)
        => label.ToUpperInvariant() switch
        {
            "TSTM" => (60f, new SKColor(0xC0, 0xE8, 0x70)),
            "MRGL" => (150f, new SKColor(0x00, 0xBB, 0x00)),
            "SLGT" => (210f, new SKColor(0xFF, 0xE1, 0x00)),
            "ENH" => (270f, new SKColor(0xFF, 0x99, 0x00)),
            "MDT" => (330f, new SKColor(0xFF, 0x66, 0x00)),
            "HIGH" => (390f, new SKColor(0xFF, 0x20, 0x20)),
            _ => (0, SKColors.Transparent)
        };

    private static string SpcBarLabel(string label)
        => label.ToUpperInvariant() switch
        {
            "TSTM" => "T'STORM",
            "MRGL" => "MARGINAL",
            "SLGT" => "SLIGHT",
            "ENH" => "ENHANCED",
            "MDT" => "MODERATE",
            "HIGH" => "HIGH",
            _ => ""
        };

    private static void DrawLocalForecastChrome(
        SKCanvas canvas,
        WeatherSnapshot snap,
        SKTypeface font,
        int width,
        SKPaint white,
        SKPaint gold)
    {
        const float citySize = 14f;
        const float titleSize = 24f;
        DrawText(
            canvas,
            snap.Place.DisplayName.ToUpperInvariant(),
            font,
            citySize,
            16,
            BaselineBelow(font, citySize, 28),
            gold);
        DrawText(
            canvas,
            PlaceNow(snap).ToString("h:mm tt", CultureInfo.InvariantCulture),
            font,
            citySize,
            width - 16,
            BaselineBelow(font, citySize, 28),
            white,
            SKTextAlign.Right);
        DrawText(canvas, "Local", font, titleSize, 16, BaselineBelow(font, titleSize, 50), white);
        DrawText(canvas, "Forecast", font, titleSize, 16, BaselineBelow(font, titleSize, 78), white);
    }

    private static void DrawLocalForecast(
        SKCanvas canvas,
        WeatherSnapshot snap,
        SKTypeface font,
        int width,
        int height,
        int radarIndex,
        int screenRepeat,
        SKPaint white,
        SKPaint gold)
    {
        var periods = snap.Periods.Take(6).ToList();
        if (periods.Count == 0)
        {
            periods = snap.Daily.Take(6).Select(day => new WeatherForecastPeriod
            {
                Name = day.Name,
                Narrative = day.Narrative,
                IconKey = day.IconKey,
                Temperature = day.High ?? day.Low ?? 0,
                IsDaytime = day.High is not null
            }).ToList();
        }

        if (periods.Count == 0)
        {
            DrawText(canvas, "NO FORECAST DATA", font, 24, 48, 200, white);
            return;
        }

        var period = periods[Math.Clamp(screenRepeat, 0, periods.Count - 1)];
        var narrative = period.Narrative.Replace("...", " ", StringComparison.Ordinal).Trim();
        var name = period.Name.Trim();
        if (name.Length == 0)
        {
            name = "Forecast";
        }

        const float fontSize = 24f;
        const float lineHeight = 34f;
        const float left = 56f;
        const float right = 72f;
        var maxWidth = Math.Max(280f, width - left - right);
        var y = BaselineBelow(font, fontSize, 128);
        DrawText(canvas, name.ToUpperInvariant() + "...", font, fontSize, left, y, gold);
        y += lineHeight + 4;

        var lines = WrapText(narrative, font, fontSize, maxWidth);
        lines = BalanceWrappedLines(lines, font, fontSize, maxWidth);
        const int visible = 7;
        var maxOffset = Math.Max(0, lines.Count - visible);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 20);
        foreach (var line in lines.Skip(offset).Take(visible))
        {
            DrawText(canvas, line, font, fontSize, left, y, white);
            y += lineHeight;
            if (y > height - 44)
            {
                break;
            }
        }
    }

    private static float BaselineBelow(SKTypeface typeface, float size, float top)
    {
        using var font = new SKFont(typeface, size);
        var ascent = font.Metrics.Ascent;
        return top - ascent + 2;
    }

    private static string FormatDayTemps(WeatherDaily day)
    {
        if (day.High is double high && day.Low is double low)
        {
            return Math.Round(high).ToString("0", CultureInfo.InvariantCulture)
                + "/"
                + Math.Round(low).ToString("0", CultureInfo.InvariantCulture);
        }

        if (day.High is double hi)
        {
            return Math.Round(hi).ToString("0", CultureInfo.InvariantCulture);
        }

        if (day.Low is double lo)
        {
            return Math.Round(lo).ToString("0", CultureInfo.InvariantCulture);
        }

        return "";
    }

    private static List<string> WrapText(string text, SKTypeface typeface, float size, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return lines;
        }

        using var skFont = new SKFont(typeface, size);
        using var measurePaint = new SKPaint { IsAntialias = true };
        var words = text.Replace("...", "... ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var word in words)
        {
            var trial = current.Length == 0 ? word : current + " " + word;
            if (TextWidth(skFont, trial, measurePaint) <= maxWidth || current.Length == 0)
            {
                current = trial;
                continue;
            }

            lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static List<string> BalanceWrappedLines(
        List<string> lines,
        SKTypeface typeface,
        float size,
        float maxWidth)
    {
        if (lines.Count < 2)
        {
            return lines;
        }

        using var font = new SKFont(typeface, size);
        using var paint = new SKPaint { IsAntialias = true };
        var last = lines[^1];
        if (TextWidth(font, last, paint) >= maxWidth * 0.35f)
        {
            return lines;
        }

        var previous = lines[^2];
        var words = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return lines;
        }

        var pulled = words[^1] + " " + last;
        var kept = string.Join(' ', words.Take(words.Length - 1));
        if (TextWidth(font, pulled, paint) <= maxWidth)
        {
            lines[^2] = kept;
            lines[^1] = pulled;
        }

        return lines;
    }

    private static float TextWidth(SKFont font, string text, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var widths = font.GetGlyphWidths(text, paint);
        var advance = 0f;
        foreach (var width in widths)
        {
            advance += width;
        }

        var ink = font.MeasureText(text, out SKRect bounds, paint);
        var visual = Math.Max(bounds.Width, Math.Abs(bounds.Right - bounds.Left));
        return Math.Max(advance, Math.Max(ink, visual));
    }

    private static void DrawHazards(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, int radarIndex, SKPaint white, SKPaint gold)
    {
        if (snap.Alerts.Count == 0)
        {
            DrawText(canvas, "NO WATCHES, WARNINGS, OR ADVISORIES", font, 18, 24, 200, gold);
            return;
        }

        var fs = Math.Max(16, width / 60);
        var wrapWidth = Math.Max(40, width - (width >= 1200 ? 160 : 80));
        var lines = new List<(bool Header, string Text)>();
        foreach (var alert in snap.Alerts.Take(5))
        {
            lines.Add((true, alert.Event.ToUpperInvariant()));
            lines.Add((false, ""));
            var body = string.IsNullOrWhiteSpace(alert.Description) ? alert.Headline : alert.Description;
            foreach (var wrap in WrapText(body.Replace('\n', ' '), font, fs, wrapWidth))
            {
                lines.Add((false, wrap.ToUpperInvariant()));
            }

            lines.Add((false, ""));
            lines.Add((false, ""));
        }

        var visible = width >= 1200 ? 16 : 12;
        var maxOffset = Math.Max(0, lines.Count - visible);
        var offset = maxOffset == 0 ? 0 : Math.Min(maxOffset, radarIndex * Math.Max(1, maxOffset) / 18);
        var y = width >= 1200 ? 90f : 70f;
        var step = fs + 12;
        foreach (var line in lines.Skip(offset).Take(visible))
        {
            if (line.Text.Length > 0)
            {
                DrawText(canvas, line.Text, font, line.Header ? fs + 4 : fs, 40, y, line.Header ? gold : white);
            }

            y += step;
        }
    }

    private void DrawRadar(SKCanvas canvas, WeatherSnapshot snap, SKTypeface font, int width, int height, int radarIndex, SKPaint white)
    {
        var ox = ContentOriginX(width);
        var dest = new SKRect(ox, 83, ox + WeatherStarRadar.ViewWidth, 83 + WeatherStarRadar.ViewHeight);
        canvas.Save();
        canvas.ClipRect(dest);

        using (var fill = new SKPaint { Color = new SKColor(0x5A, 0x6A, 0x7A) })
        {
            canvas.DrawRect(dest, fill);
        }

        var (map, overlay) = _assets.RadarBaseMap(snap.Place.Latitude, snap.Place.Longitude);
        if (map is not null)
        {
            DrawBitmap(canvas, map, dest);
        }

        if (snap.Radar.Count == 0)
        {
            canvas.Restore();
            DrawText(canvas, snap.IsUnitedStates ? "RADAR UNAVAILABLE" : "NO LOCAL RADAR", font, 24, dest.Left + 24, dest.Top + 80, white);
            return;
        }

        var frame = snap.Radar[Math.Abs(radarIndex) % snap.Radar.Count];
        using var bmp = SKBitmap.Decode(frame.Image);
        if (bmp is not null)
        {
            WeatherStarRadar.PunchBlack(bmp);
            DrawBitmap(canvas, bmp, dest);
        }

        if (overlay is not null)
        {
            DrawBitmap(canvas, overlay, dest);
        }

        canvas.Restore();
        DrawText(
            canvas,
            InPlace(frame.Time, snap).ToString("h:mm tt", CultureInfo.InvariantCulture),
            font,
            16,
            dest.Left + 8,
            dest.Bottom - 10,
            white);
    }

    private static string Title(WeatherStarScreen screen)
        => screen switch
        {
            WeatherStarScreen.Current => "CURRENT CONDITIONS",
            WeatherStarScreen.Observations => "LATEST OBSERVATIONS",
            WeatherStarScreen.Hourly => "HOURLY FORECAST",
            WeatherStarScreen.HourlyGraph => "HOURLY GRAPH",
            WeatherStarScreen.LocalForecast => "LOCAL FORECAST",
            WeatherStarScreen.ExtendedForecast => "EXTENDED FORECAST",
            WeatherStarScreen.Hazards => "WEATHER ALERTS",
            WeatherStarScreen.Radar => "LOCAL RADAR",
            WeatherStarScreen.Regional => "REGIONAL FORECAST",
            WeatherStarScreen.Almanac => "ALMANAC",
            WeatherStarScreen.Travel => "TRAVEL CITIES",
            WeatherStarScreen.SpcOutlook => "STORM OUTLOOK",
            _ => "WEATHER"
        };

    private static readonly SKSamplingOptions BitmapSampling = new(SKFilterMode.Linear);

    private static void DrawBitmap(SKCanvas canvas, SKBitmap bitmap, SKRect dest)
        => canvas.DrawBitmap(bitmap, dest, BitmapSampling);

    private static void DrawText(
        SKCanvas canvas,
        string text,
        SKTypeface typeface,
        float size,
        float x,
        float y,
        SKPaint color,
        SKTextAlign align = SKTextAlign.Left)
    {
        using var font = new SKFont(typeface, size);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 220), IsAntialias = true };
        canvas.DrawText(text, x + 2, y + 2, align, font, shadow);
        canvas.DrawText(text, x, y, align, font, color);
    }
}
