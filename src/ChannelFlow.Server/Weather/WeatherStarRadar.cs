using SkiaSharp;

namespace FinTv.Weather;

/// <summary>
/// WeatherStar 4000 local-radar math from ws4kp (map tiles + IEM N0R overlay).
/// </summary>
internal static class WeatherStarRadar
{
    public const int TileWidth = 680;
    public const int TileHeight = 387;
    public const int TileCountX = 10;
    public const int TileCountY = 11;
    public const int MapWidth = 6800;
    public const int MapHeight = 4255;
    public const int ViewWidth = 640;
    public const int ViewHeight = 367;
    public const int RadarFullWidth = 2550;
    public const int RadarFullHeight = 1600;
    public const int RadarSourceWidth = 240;
    public const int RadarSourceHeight = 163;
    public const int RadarOffsetX = 240;
    public const int RadarOffsetY = 138;

    public static (float X, float Y) MapOrigin(double latitude, double longitude)
    {
        var y = Coerce(0, (-145.095 * latitude + 7377.117) - 27 - (TileHeight / 2.0), MapHeight - TileHeight);
        var x = Coerce(0, (111.407 * longitude + 14220.972) + 4 - (TileWidth / 2.0), MapWidth - TileWidth);
        return ((float)x, (float)y);
    }

    public static byte[] CropReflectivity(byte[] png, double latitude, double longitude)
    {
        using var source = SKBitmap.Decode(png);
        if (source is null)
        {
            return png;
        }

        using var stretched = new SKBitmap(RadarFullWidth, RadarFullHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(stretched))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(
                source,
                new SKRect(0, 0, stretched.Width, stretched.Height),
                new SKSamplingOptions(SKFilterMode.Nearest));
        }

        var y = Coerce(0, (51 - latitude) * 61.4481 - RadarOffsetY, 6000);
        var x = Coerce(0, ((-129.138 - longitude) * 42.1768) * -1 - RadarOffsetX, 2800);
        var cropX = (int)Math.Round(x);
        var cropY = (int)Math.Round(y);
        cropX = Math.Clamp(cropX, 0, Math.Max(0, stretched.Width - RadarSourceWidth));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, stretched.Height - RadarSourceHeight));
        var cropW = Math.Min(RadarSourceWidth, stretched.Width - cropX);
        var cropH = Math.Min(RadarSourceHeight, stretched.Height - cropY);

        var info = new SKImageInfo(cropW, cropH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var crop = new SKBitmap(info);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(
                stretched,
                new SKRect(cropX, cropY, cropX + cropW, cropY + cropH),
                new SKRect(0, 0, cropW, cropH),
                new SKSamplingOptions(SKFilterMode.Nearest));
        }

        RecolorReflectivity(crop);
        using var image = SKImage.FromBitmap(crop);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    public static void PunchBlack(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 12 && pixel.Green < 12 && pixel.Blue < 12)
                {
                    bitmap.SetPixel(x, y, SKColors.Transparent);
                }
            }
        }
    }

    private static void RecolorReflectivity(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, MapReflectivity(bitmap.GetPixel(x, y)));
            }
        }
    }

    private static SKColor MapReflectivity(SKColor pixel)
    {
        var r = pixel.Red;
        var g = pixel.Green;
        var b = pixel.Blue;
        if ((r == 0 && g == 0 && b == 0)
            || (r == 0 && g == 236 && b == 236)
            || (r == 1 && g == 160 && b == 246)
            || (r == 0 && g == 0 && b == 246))
        {
            return SKColors.Transparent;
        }

        if (r == 0 && g == 255 && b == 0)
        {
            return new SKColor(49, 210, 22);
        }

        if (r == 0 && g == 200 && b == 0)
        {
            return new SKColor(0, 142, 0);
        }

        if (r == 0 && g == 144 && b == 0)
        {
            return new SKColor(20, 90, 15);
        }

        if (r == 255 && g == 255 && b == 0)
        {
            return new SKColor(10, 40, 10);
        }

        if (r == 231 && g == 192 && b == 0)
        {
            return new SKColor(196, 179, 70);
        }

        if (r == 255 && g == 144 && b == 0)
        {
            return new SKColor(190, 72, 19);
        }

        if ((r == 214 && g == 0 && b == 0) || (r == 255 && g == 0 && b == 0))
        {
            return new SKColor(171, 14, 14);
        }

        if ((r == 192 && g == 0 && b == 0) || (r == 255 && g == 0 && b == 255))
        {
            return new SKColor(115, 31, 4);
        }

        return pixel.WithAlpha(255);
    }

    private static double Coerce(double low, double value, double high)
        => Math.Max(low, Math.Min(value, high));
}
