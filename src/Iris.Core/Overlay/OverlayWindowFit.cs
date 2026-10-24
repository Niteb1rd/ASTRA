namespace Iris.Core.Overlay;

/// <summary>
/// Sizing an overlay window to what it draws. A transparent (layered) window costs Windows work in proportion to its AREA on every redraw, however little
/// changed in it, so the live overlay does not keep one window as big as the game: the brackets live in a window just big enough for them, and the panel in
/// another (the panel almost never changes, the brackets move up to 120 times a second). These are the rectangle rules, without any windowing code. All
/// rectangles are in game-window pixels.
/// </summary>
public static class OverlayWindowFit
{
    /// <summary>The window may be this much (in square pixels) larger than the margin rule asks for before it is shrunk again: tiny windows are never worth moving.</summary>
    public const double SlackArea = 256.0 * 256.0;

    /// <summary>A window is shrunk when it is more than this many times the area of what is needed (with the margin).</summary>
    public const double ShrinkFactor = 2.0;

    /// <summary>The smallest rectangle holding all <paramref name="rects"/>; null for none (empty rectangles are ignored).</summary>
    public static PixelRect? Union(IEnumerable<PixelRect> rects)
    {
        double left = 0, top = 0, right = 0, bottom = 0;
        var any = false;
        foreach (var r in rects)
        {
            if (r.IsEmpty)
                continue;
            if (!any)
            {
                (left, top, right, bottom) = (r.X, r.Y, r.Right, r.Bottom);
                any = true;
                continue;
            }

            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return any ? PixelRect.FromEdges(left, top, right, bottom) : null;
    }

    public static PixelRect Inflate(PixelRect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    /// <summary>The part of <paramref name="a"/> inside <paramref name="bounds"/>; an empty rectangle when they do not overlap.</summary>
    public static PixelRect Clip(PixelRect a, PixelRect bounds)
    {
        var left = Math.Max(a.X, bounds.X);
        var top = Math.Max(a.Y, bounds.Y);
        var right = Math.Min(a.Right, bounds.Right);
        var bottom = Math.Min(a.Bottom, bounds.Bottom);
        return right <= left || bottom <= top ? default : PixelRect.FromEdges(left, top, right, bottom);
    }

    public static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>
    /// The window rectangle (inside <paramref name="bounds"/>, the game window) for content that needs <paramref name="needed"/>. The current rectangle is kept
    /// when it still holds the content and is not much larger than needed (so a bracket that moves a little does not move the window); otherwise the window
    /// becomes the needed rectangle plus <paramref name="margin"/> on every side, clipped to the bounds. An empty result means there is nothing to draw.
    /// </summary>
    public static PixelRect Choose(PixelRect? current, PixelRect needed, PixelRect bounds, double margin)
    {
        var clipped = Clip(needed, bounds);
        if (clipped.IsEmpty)
            return default;

        var grown = Clip(Inflate(clipped, margin), bounds);
        if (current is { IsEmpty: false } have
            && Contains(have, clipped)
            && Contains(bounds, have)
            && have.Width * have.Height <= ShrinkFactor * grown.Width * grown.Height + SlackArea)
            return have;
        return grown;
    }
}
