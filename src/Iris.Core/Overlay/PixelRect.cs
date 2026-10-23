namespace Iris.Core.Overlay;

/// <summary>
/// A rectangle in physical screen pixels, or in game-window pixels (origin at the window's top-left corner) when it belongs to the
/// <see cref="OverlayModel"/>. Plain numbers, so the overlay math can be tested without any windowing code.
/// </summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>A rectangle from its edges, as Windows reports them (left, top, right, bottom).</summary>
    public static PixelRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);
}
