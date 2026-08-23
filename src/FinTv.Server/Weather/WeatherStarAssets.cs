using System.IO.Compression;
using System.Text;
using FinTv.Services;
using Microsoft.AspNetCore.Hosting;
using SkiaSharp;

namespace FinTv.Weather;

public sealed class WeatherStarAssets : IDisposable
{
    private readonly Dictionary<string, SKBitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SKTypeface> _typefaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ws4Root;
    private readonly string _ws3Root;

    public WeatherStarAssets(IWebHostEnvironment env)
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "weatherstar"),
            Path.Combine(env.ContentRootPath, "wwwroot", "weatherstar"),
            Path.Combine(env.ContentRootPath, "weatherstar")
        };
        var vendor = new[]
        {
            Path.Combine(env.ContentRootPath, "..", "..", "vendor"),
            Path.Combine(env.ContentRootPath, "vendor"),
            "/app/vendor"
        };

        _ws4Root = roots.Select(r => Path.Combine(r, "ws4kp")).FirstOrDefault(Directory.Exists)
            ?? vendor.Select(r => Path.Combine(r, "ws4kp", "server")).FirstOrDefault(Directory.Exists)
            ?? Path.Combine(env.ContentRootPath, "wwwroot", "weatherstar", "ws4kp");
        _ws3Root = roots.Select(r => Path.Combine(r, "ws3kp")).FirstOrDefault(Directory.Exists)
            ?? vendor.Select(r => Path.Combine(r, "ws3kp", "server")).FirstOrDefault(Directory.Exists)
            ?? Path.Combine(env.ContentRootPath, "wwwroot", "weatherstar", "ws3kp");
    }

    public SKTypeface Font(WeatherStarDockerVariant skin, bool large = false)
    {
        var key = (skin == WeatherStarDockerVariant.Ws3kp ? "3000" : "4000") + (large ? "-lg" : "");
        if (_typefaces.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var names = skin == WeatherStarDockerVariant.Ws3kp
            ? (large
                ? new[] { "Star3000 Large.ttf", "Star3000.ttf" }
                : new[] { "Star3000.ttf", "Star3000 Small.ttf" })
            : (large
                ? new[] { "Star4000 Large.ttf", "Star4000 Large.woff", "Star4000.woff" }
                : new[] { "Star4000.ttf", "Star4000.woff", "Star4000 Small.woff" });
        var root = skin == WeatherStarDockerVariant.Ws3kp ? _ws3Root : _ws4Root;
        foreach (var name in names)
        {
            var path = FindFile(root, name);
            if (path is null)
            {
                continue;
            }

            var face = LoadTypeface(path);
            if (face is not null)
            {
                _typefaces[key] = face;
                return face;
            }
        }

        var fallback = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
        _typefaces[key] = fallback;
        return fallback;
    }

    public SKBitmap? Background(WeatherStarDockerVariant skin, bool wide, WeatherStarScreen screen)
    {
        var file = screen switch
        {
            WeatherStarScreen.HourlyGraph or WeatherStarScreen.Travel => wide ? "1-chart-wide.png" : "1-chart.png",
            WeatherStarScreen.Hazards => wide ? "7-wide.png" : "7.png",
            WeatherStarScreen.Radar => wide ? "4-wide.png" : "4.png",
            WeatherStarScreen.ExtendedForecast or WeatherStarScreen.LocalForecast => wide ? "4-wide.png" : "4.png",
            WeatherStarScreen.Regional => "2.png",
            WeatherStarScreen.SpcOutlook => "6.png",
            _ => wide ? "1-wide.png" : "1.png"
        };
        var root = skin == WeatherStarDockerVariant.Ws3kp ? _ws3Root : _ws4Root;
        return Bitmap(FindFile(root, file) ?? FindFile(root, "1.png") ?? FindFile(root, "1-wide.png"));
    }

    public SKBitmap? Icon(string iconKey)
    {
        var name = iconKey.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? iconKey : iconKey + ".gif";
        var path = FindFile(_ws4Root, name) ?? FindFile(_ws3Root, name);
        return Bitmap(path);
    }

    public (SKBitmap? Map, SKBitmap? Overlay) RadarBaseMap(double latitude, double longitude)
    {
        var key = $"radar-map:{latitude:F2}:{longitude:F2}";
        var overlayKey = $"radar-overlay:{latitude:F2}:{longitude:F2}";
        if (_bitmaps.TryGetValue(key, out var mapHit))
        {
            _bitmaps.TryGetValue(overlayKey, out var overlayHit);
            return (mapHit, overlayHit);
        }

        var origin = WeatherStarRadar.MapOrigin(latitude, longitude);
        var map = StitchRadarTiles("map", origin.X, origin.Y);
        var overlay = StitchRadarTiles("overlay", origin.X, origin.Y);
        if (overlay is not null)
        {
            WeatherStarRadar.PunchBlack(overlay);
        }

        if (map is not null)
        {
            _bitmaps[key] = map;
        }

        if (overlay is not null)
        {
            _bitmaps[overlayKey] = overlay;
        }

        return (map, overlay);
    }

    private SKBitmap? StitchRadarTiles(string kind, float originX, float originY)
    {
        var shiftX = originX % WeatherStarRadar.TileWidth;
        if (shiftX < 0)
        {
            shiftX += WeatherStarRadar.TileWidth;
        }

        var shiftY = originY % WeatherStarRadar.TileHeight;
        if (shiftY < 0)
        {
            shiftY += WeatherStarRadar.TileHeight;
        }

        var tileX0 = (int)Math.Floor(originX / WeatherStarRadar.TileWidth);
        var tileY0 = (int)Math.Floor(originY / WeatherStarRadar.TileHeight);
        var info = new SKImageInfo(
            WeatherStarRadar.ViewWidth,
            WeatherStarRadar.ViewHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul);
        var dest = new SKBitmap(info);
        using var canvas = new SKCanvas(dest);
        canvas.Clear(kind == "map" ? new SKColor(0x5A, 0x6A, 0x7A) : SKColors.Transparent);

        var drew = false;
        for (var row = 0; row <= 3; row++)
        {
            for (var col = 0; col <= 2; col++)
            {
                var tile = RadarTileBitmap(kind, tileY0 + row, tileX0 + col);
                if (tile is null)
                {
                    continue;
                }

                var x = col * WeatherStarRadar.TileWidth - shiftX;
                var y = row * WeatherStarRadar.TileHeight - shiftY;
                canvas.DrawBitmap(tile, x, y, new SKSamplingOptions(SKFilterMode.Nearest));
                drew = true;
            }
        }

        if (!drew)
        {
            dest.Dispose();
            return null;
        }

        return dest;
    }

    private SKBitmap? RadarTileBitmap(string kind, int tileY, int tileX)
    {
        if (tileX < 0 || tileY < 0 || tileX > WeatherStarRadar.TileCountX || tileY > WeatherStarRadar.TileCountY)
        {
            return null;
        }

        var name = $"{kind}-{tileY}-{tileX}.webp";
        var path = Path.Combine(_ws4Root, "images", "maps", "radar", name);
        return Bitmap(File.Exists(path) ? path : FindFile(_ws4Root, name));
    }

    public void Dispose()
    {
        foreach (var bmp in _bitmaps.Values)
        {
            bmp.Dispose();
        }

        foreach (var face in _typefaces.Values)
        {
            face.Dispose();
        }
    }

    private SKBitmap? Bitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (_bitmaps.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var bmp = SKBitmap.Decode(path);
        if (bmp is not null)
        {
            _bitmaps[path] = bmp;
        }

        return bmp;
    }

    private static SKTypeface? LoadTypeface(string path)
    {
        if (path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase))
        {
            var ttf = WoffConverter.TryToSfnt(File.ReadAllBytes(path));
            if (ttf is not null)
            {
                return SKTypeface.FromData(SKData.CreateCopy(ttf));
            }
        }

        return SKTypeface.FromFile(path);
    }

    private static string? FindFile(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var direct = Path.Combine(root, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (var folder in new[]
                 {
                     "fonts",
                     "images/backgrounds",
                     "images/icons/current-conditions",
                     "images/maps/radar",
                     "backgrounds",
                     "icons"
                 })
        {
            var candidate = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }
}

internal static class WoffConverter
{
    public static byte[]? TryToSfnt(byte[] woff)
    {
        if (woff.Length < 44 || Encoding.ASCII.GetString(woff, 0, 4) != "wOFF")
        {
            return null;
        }

        try
        {
            var flavor = ReadU32(woff, 4);
            var numTables = ReadU16(woff, 12);
            var sfnt = new MemoryStream();
            using var writer = new BinaryWriter(sfnt, Encoding.ASCII, leaveOpen: true);
            WriteU32(writer, flavor);
            WriteU16(writer, numTables);
            var entrySelector = (ushort)Math.Floor(Math.Log2(numTables));
            var searchRange = (ushort)((1 << entrySelector) * 16);
            WriteU16(writer, searchRange);
            WriteU16(writer, entrySelector);
            WriteU16(writer, (ushort)(numTables * 16 - searchRange));

            var tables = new List<(byte[] Tag, byte[] Data)>();
            for (var i = 0; i < numTables; i++)
            {
                var offset = 44 + i * 20;
                var tag = woff[offset..(offset + 4)];
                var origOffset = (int)ReadU32(woff, offset + 4);
                var compLength = (int)ReadU32(woff, offset + 8);
                var origLength = (int)ReadU32(woff, offset + 12);
                var packed = woff[origOffset..(origOffset + compLength)];
                byte[] data;
                if (compLength == origLength)
                {
                    data = packed;
                }
                else
                {
                    using var input = new MemoryStream(packed);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    data = output.ToArray();
                }

                if (data.Length != origLength)
                {
                    Array.Resize(ref data, origLength);
                }

                tables.Add((tag, data));
            }

            var dataOffset = 12 + numTables * 16;
            dataOffset = Align4(dataOffset);
            var tableOffsets = new int[tables.Count];
            var cursor = dataOffset;
            for (var i = 0; i < tables.Count; i++)
            {
                cursor = Align4(cursor);
                tableOffsets[i] = cursor;
                cursor += tables[i].Data.Length;
            }

            for (var i = 0; i < tables.Count; i++)
            {
                writer.Write(tables[i].Tag);
                WriteU32(writer, Checksum(tables[i].Data));
                WriteU32(writer, (uint)tableOffsets[i]);
                WriteU32(writer, (uint)tables[i].Data.Length);
            }

            while (sfnt.Length < dataOffset)
            {
                writer.Write((byte)0);
            }

            for (var i = 0; i < tables.Count; i++)
            {
                while (sfnt.Length < tableOffsets[i])
                {
                    writer.Write((byte)0);
                }

                writer.Write(tables[i].Data);
            }

            while (sfnt.Length % 4 != 0)
            {
                writer.Write((byte)0);
            }

            return sfnt.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            sum += ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3];
        }

        return sum;
    }

    private static uint ReadU32(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static ushort ReadU16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteU32(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteU16(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }
}
