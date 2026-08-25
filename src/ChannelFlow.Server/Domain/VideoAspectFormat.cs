using System.Globalization;

namespace FinTv.Domain;

/// <summary>
/// Classifies video picture format as <c>16:9</c>, <c>4:3</c>, or <c>other</c>.
/// Nearby ratios (1.85:1, 1.37:1, …) snap to the closer broadcast format;
/// far-off pictures (2.39:1, 9:16, 1:1, …) stay <c>other</c>.
/// </summary>
public static class VideoAspectFormat
{
    public const string SixteenNine = "16:9";

    public const string FourThree = "4:3";

    public const string Other = "other";

    /// <summary>
    /// Split between 4:3-like (~1.333) and 16:9-like (~1.778) pictures for overlay pinning.
    /// NTSC 720×480 (1.5) stays 4:3-like; 2.39 and 9:16 stay on their true picture box.
    /// </summary>
    public const double WidescreenCutoff = 1.55;

    /// <summary>
    /// Relative error allowed when snapping a ratio onto 4:3 or 16:9.
    /// 1.85:1 (~4% from 16:9) snaps; 2.00:1 and 2.39:1 stay <c>other</c>.
    /// </summary>
    public const double SnapRelativeError = 0.10;

    private static readonly double FourThreeRatio = 4d / 3d;

    private static readonly double SixteenNineRatio = 16d / 9d;

    /// <summary>
    /// Prefers an explicit 16:9/4:3 label (covers anamorphic SD), then pixel dimensions.
    /// Returns null when there is no video size or aspect metadata.
    /// </summary>
    public static string? Classify(string? aspectRatio, int? width, int? height)
    {
        var fromLabel = ClassifyLabel(aspectRatio);
        if (fromLabel is SixteenNine or FourThree)
        {
            return fromLabel;
        }

        if (width is > 0 && height is > 0)
        {
            return ClassifyRatio(width.Value / (double)height.Value);
        }

        return fromLabel;
    }

    /// <summary>
    /// True for 4:3, academy, SD, and portrait pictures so overlays stay on the image
    /// when a precise picture box is not used. 16:9 and wider classified formats are false.
    /// Missing metadata is treated as widescreen.
    /// </summary>
    public static bool IsFourThreeLike(string? aspectRatio, int? width, int? height)
    {
        var classified = Classify(aspectRatio, width, height);
        if (classified == FourThree)
        {
            return true;
        }

        if (classified == SixteenNine)
        {
            return false;
        }

        if (width is > 0 && height is > 0)
        {
            var ratio = width.Value / (double)height.Value;
            return ratio > 0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio) && ratio < WidescreenCutoff;
        }

        if (TryParseRatio(aspectRatio, out var parsed))
        {
            return parsed < WidescreenCutoff;
        }

        return false;
    }

    /// <summary>
    /// Picture aspect used after scale-to-fit. Named 4:3/16:9 labels win (anamorphic SD).
    /// Other labels and pixel sizes keep their true ratio so the bug sits on the image,
    /// not on letterbox/pillarbox bars.
    /// </summary>
    public static bool TryGetDisplayRatio(string? aspectRatio, int? width, int? height, out double ratio)
    {
        var named = NamedBroadcastLabel(aspectRatio);
        var namedRatio = named == FourThree
            ? FourThreeRatio
            : named == SixteenNine ? SixteenNineRatio : (double?)null;

        if (width is > 0 && height is > 0)
        {
            var pixelRatio = width.Value / (double)height.Value;
            if (pixelRatio > 0 && !double.IsNaN(pixelRatio) && !double.IsInfinity(pixelRatio))
            {
                // Anamorphic SD: 720×480 pixels with a 16:9 or 4:3 label.
                if (namedRatio is double broadcast
                    && RelativeError(pixelRatio, broadcast) > SnapRelativeError
                    && pixelRatio < WidescreenCutoff)
                {
                    ratio = broadcast;
                    return true;
                }

                ratio = pixelRatio;
                return true;
            }
        }

        if (namedRatio is double fromLabel)
        {
            ratio = fromLabel;
            return true;
        }

        if (TryParseRatio(aspectRatio, out ratio))
        {
            return true;
        }

        ratio = 0;
        return false;
    }

    private static string? ClassifyLabel(string? value)
    {
        var named = NamedBroadcastLabel(value);
        if (named is SixteenNine or FourThree or Other)
        {
            return named;
        }

        if (TryParseRatio(value, out var ratio))
        {
            return ClassifyRatio(ratio);
        }

        return string.IsNullOrWhiteSpace(value) ? null : Other;
    }

    private static string? NamedBroadcastLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = NormalizeLabel(value);
        if (text is "16:9" or "1.78:1" or "1.77:1" or "1.78" or "1.77" or "1.777" or "1.778")
        {
            return SixteenNine;
        }

        if (text is "4:3" or "1.33:1" or "1.33" or "1.333" or "1.334")
        {
            return FourThree;
        }

        if (text is "other" or "unknown")
        {
            return Other;
        }

        return null;
    }

    private static string ClassifyRatio(double ratio)
    {
        if (ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            return Other;
        }

        var error169 = RelativeError(ratio, SixteenNineRatio);
        var error43 = RelativeError(ratio, FourThreeRatio);
        if (error169 <= SnapRelativeError && error169 <= error43)
        {
            return SixteenNine;
        }

        if (error43 <= SnapRelativeError)
        {
            return FourThree;
        }

        return Other;
    }

    private static double RelativeError(double actual, double expected)
        => Math.Abs(actual - expected) / expected;

    private static string NormalizeLabel(string value)
        => value.Trim().ToLowerInvariant()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("×", ":", StringComparison.Ordinal)
            .Replace("x", ":", StringComparison.Ordinal)
            .Replace("/", ":", StringComparison.Ordinal);

    private static bool TryParseRatio(string? value, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = NormalizeLabel(value);
        var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && TryParseNumber(parts[0], out var left)
            && TryParseNumber(parts[1], out var right)
            && right != 0)
        {
            ratio = left / right;
            return ratio > 0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio);
        }

        if (parts.Length == 1 && TryParseNumber(parts[0], out ratio) && ratio > 0)
        {
            return !double.IsNaN(ratio) && !double.IsInfinity(ratio);
        }

        return false;
    }

    private static bool TryParseNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
