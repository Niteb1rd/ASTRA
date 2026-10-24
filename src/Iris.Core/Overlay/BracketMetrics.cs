namespace Iris.Core.Overlay;

/// <summary>
/// The size of one bracket pair around a nameplate box, in pixels: the numbers the overlay draws with, in one place so that the painter and the code that sizes the
/// overlay window to what is drawn (<see cref="OverlayWindowFit"/>) agree. The brackets are a little taller than the box (12 % on each side), a quarter as wide as
/// they are tall, 18 % of the box height away from the box, with a colored line of at least 2 px and a dark outline half as thick on each side of it.
/// </summary>
public readonly record struct BracketMetrics(double Top, double Height, double Width, double Gap, double Stroke, double Outline)
{
    public static BracketMetrics For(PixelRect box)
    {
        var padding = box.Height * 0.12;
        var height = box.Height + 2 * padding;
        var width = height * 0.24;
        var stroke = Math.Max(2.0, height * 0.06);
        return new BracketMetrics(
            Top: box.Y - padding,
            Height: height,
            Width: width,
            Gap: box.Height * 0.18,
            Stroke: stroke,
            Outline: Math.Max(1.0, stroke * 0.5));
    }

    /// <summary>The left edge of the left bracket.</summary>
    public double LeftX(PixelRect box) => box.X - Gap - Width;

    /// <summary>The left edge of the right bracket.</summary>
    public double RightX(PixelRect box) => box.Right + Gap;

    /// <summary>Everything drawn for the box: both brackets with their outline (a round pen sticks out half its thickness).</summary>
    public PixelRect Extent(PixelRect box)
    {
        var half = Stroke / 2 + Outline;
        var left = LeftX(box) - half;
        var right = RightX(box) + Width + half;
        return new PixelRect(left, Top - half, right - left, Height + 2 * half);
    }

    /// <summary>The extent of the brackets of a box, without building the metrics first.</summary>
    public static PixelRect ExtentOf(PixelRect box) => For(box).Extent(box);
}
