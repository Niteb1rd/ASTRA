namespace Iris.Core.Overlay;

/// <summary>
/// The size and place of one bracket pair around a nameplate, in pixels: the numbers the overlay draws with, in one place so that the painter, the code that
/// sizes the overlay window to what is drawn (<see cref="OverlayWindowFit"/>) and the capture mask agree.
/// <list type="bullet">
/// <item><b>Fixed size.</b> Every plate gets the same bracket, whatever its height: the one of a one-row plate at 1440 (a 41 px high box): height 41 x 1.24,
/// width 0.24 x height, colored line max(2, 6 % of the height), dark outline half as thick on each side of it. A plate with several name rows (up to about
/// 80 px high) does NOT get taller brackets. All of it is scaled by screen height / 1440 (<see cref="Scale"/>).</item>
/// <item><b>Centered</b> vertically on the whole plate box (name rows and distance line).</item>
/// <item><b>Clear of the text.</b> The nameplate rows are centered over each other, so the plate box's left and right edges are those of the widest row.
/// The brackets stand so that the nearest pixel they draw (the end of the bracket arm plus half the line and the outline) is
/// <see cref="MinClearPx"/> x scale away from that text.</item>
/// </list>
/// </summary>
public readonly record struct BracketMetrics(double Top, double Height, double Width, double Gap, double Stroke, double Outline)
{
    /// <summary>The screen height the numbers are given for.</summary>
    public const double ReferenceScreenHeight = 1440.0;

    /// <summary>The height of the one-row plate box at the reference height that the bracket is sized for.</summary>
    public const double ReferenceBoxHeight = 41.0;

    /// <summary>The least free space, at the reference height, between the plate's text and the nearest drawn pixel of a bracket.</summary>
    public const double MinClearPx = 10.0;

    /// <summary>screen height / 1440 (1 for a height that is not known).</summary>
    public static double Scale(double screenHeight) => screenHeight > 0 ? screenHeight / ReferenceScreenHeight : 1.0;

    /// <param name="plateBox">The whole plate: name rows and distance line (game-window pixels).</param>
    /// <param name="screenHeight">The game window's height in pixels (the capture height): the bracket scales with it.</param>
    public static BracketMetrics For(PixelRect plateBox, double screenHeight)
    {
        var scale = Scale(screenHeight);

        // the current formulas applied to a one-row plate box at 1440, then scaled
        var height = ReferenceBoxHeight * 1.24;
        var stroke = Math.Max(2.0, height * 0.06);
        var outline = Math.Max(1.0, stroke * 0.5);
        var width = height * 0.24;

        height *= scale;
        width *= scale;
        stroke *= scale;
        outline *= scale;

        return new BracketMetrics(
            Top: plateBox.Y + plateBox.Height / 2 - height / 2,
            Height: height,
            Width: width,
            Gap: MinClearPx * scale + stroke / 2 + outline,
            Stroke: stroke,
            Outline: outline);
    }

    /// <summary>The left edge of the left bracket.</summary>
    public double LeftX(PixelRect box) => box.X - Gap - Width;

    /// <summary>The left edge of the right bracket.</summary>
    public double RightX(PixelRect box) => box.Right + Gap;

    /// <summary>How far the drawing sticks out beyond the bracket's own line: a round pen sticks out half its thickness, and the dark outline lies outside the colored line.</summary>
    public double Bleed => Stroke / 2 + Outline;

    /// <summary>Everything drawn for the box: both brackets with their outline.</summary>
    public PixelRect Extent(PixelRect box)
    {
        var half = Bleed;
        var left = LeftX(box) - half;
        var right = RightX(box) + Width + half;
        return new PixelRect(left, Top - half, right - left, Height + 2 * half);
    }

    /// <summary>The extent of the brackets of a box, without building the metrics first.</summary>
    public static PixelRect ExtentOf(PixelRect plateBox, double screenHeight) => For(plateBox, screenHeight).Extent(plateBox);
}
