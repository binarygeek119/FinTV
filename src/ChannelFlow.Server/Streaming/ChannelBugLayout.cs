using System.Globalization;
using FinTv.Domain;

namespace FinTv.Streaming;

/// <summary>
/// Channel-bug coordinates on the letterboxed/pillarboxed output frame.
/// The bug sits on the scaled picture (4:3, 16:9, 2.39:1, 9:16, …), not on the bars.
/// </summary>
public static class ChannelBugLayout
{
    public const int Margin = 24;

    /// <summary>
    /// Overlay opacity for the channel bug (1 = opaque). PNG alpha is multiplied by this.
    /// </summary>
    public const double Opacity = 0.82;

    /// <summary>
    /// Fade duration when the bug appears after a commercial or disappears before one.
    /// </summary>
    public const double FadeSeconds = 3.0;

    public static string AlphaFilters(bool fadeIn, bool fadeOut, double outputDurationSeconds)
    {
        var filters = $"colorchannelmixer=aa={Opacity.ToString(CultureInfo.InvariantCulture)}";
        if (!fadeIn && !fadeOut)
        {
            return filters;
        }

        var fade = FadeSeconds;
        if (outputDurationSeconds < fade * 2)
        {
            fade = Math.Max(0.2, outputDurationSeconds / 2.0);
        }

        var fadeText = fade.ToString("0.###", CultureInfo.InvariantCulture);
        if (fadeIn)
        {
            filters += $",fade=t=in:st=0:d={fadeText}:alpha=1";
        }

        if (fadeOut)
        {
            var start = Math.Max(0, outputDurationSeconds - fade).ToString("0.###", CultureInfo.InvariantCulture);
            filters += $",fade=t=out:st={start}:d={fadeText}:alpha=1";
        }

        return filters;
    }

    public static string OverlayExpression(
        BugPlacementMode placement,
        AspectRatioMode channelAspect,
        int canvasWidth,
        int canvasHeight,
        string? sourceAspectRatio,
        int? sourceWidth,
        int? sourceHeight)
    {
        if (VideoAspectFormat.TryGetDisplayRatio(sourceAspectRatio, sourceWidth, sourceHeight, out var ratio))
        {
            var picture = FitInside(canvasWidth, canvasHeight, ratio);
            return InsidePicture(placement, picture);
        }

        if (channelAspect == AspectRatioMode.FourThree)
        {
            return InsidePicture(placement, new Rect(0, 0, canvasWidth, canvasHeight));
        }

        return OutsideFourThree(placement);
    }

    private static string InsidePicture(BugPlacementMode placement, Rect picture)
    {
        var left = picture.X + Margin;
        var top = picture.Y + Margin;
        var right = picture.Right - Margin;
        var bottom = picture.Bottom - Margin;
        return Corner(placement, left.ToString(CultureInfo.InvariantCulture), top.ToString(CultureInfo.InvariantCulture), $"{right}-w", $"{bottom}-h");
    }

    private static string OutsideFourThree(BugPlacementMode placement)
        => Corner(placement, $"{Margin}", $"{Margin}", $"W-w-{Margin}", $"H-h-{Margin}");

    private static string Corner(BugPlacementMode placement, string left, string top, string right, string bottom)
        => placement switch
        {
            BugPlacementMode.TopLeft => $"{left}:{top}",
            BugPlacementMode.TopRight => $"{right}:{top}",
            BugPlacementMode.BottomLeft => $"{left}:{bottom}",
            BugPlacementMode.BottomRight => $"{right}:{bottom}",
            BugPlacementMode.None => string.Empty,
            _ => $"{right}:{bottom}"
        };

    private static Rect FitInside(int canvasWidth, int canvasHeight, double sourceRatio)
    {
        var canvasRatio = canvasWidth / (double)canvasHeight;
        int pictureWidth;
        int pictureHeight;
        if (sourceRatio > canvasRatio)
        {
            pictureWidth = canvasWidth;
            pictureHeight = Math.Max(1, (int)Math.Round(canvasWidth / sourceRatio, MidpointRounding.AwayFromZero));
        }
        else
        {
            pictureHeight = canvasHeight;
            pictureWidth = Math.Max(1, (int)Math.Round(canvasHeight * sourceRatio, MidpointRounding.AwayFromZero));
        }

        var x = (canvasWidth - pictureWidth) / 2;
        var y = (canvasHeight - pictureHeight) / 2;
        return new Rect(x, y, pictureWidth, pictureHeight);
    }

    private readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }
}
