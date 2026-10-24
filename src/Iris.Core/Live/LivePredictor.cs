using Iris.Core.Overlay;

namespace Iris.Core.Live;

/// <summary>
/// The "render tick": the overlay model for the moment a frame is drawn (monitor refresh). The snapshot's brackets stand where their plates were last TRACKED
/// (the tracking ticks, <see cref="PlateTemplateTracker"/>, or a detection); every bracket is moved from there along its tracked velocity (constant velocity)
/// to "now", for the time since that position, but at most <see cref="LiveSettings.RenderExtrapolationMs"/> (50 ms) ahead. This is arithmetic on a handful
/// of numbers: no capture, no image work, so it can run at any refresh rate. It is a prediction, not a measurement: the next tracking tick puts the bracket right.
/// </summary>
public static class LivePredictor
{
    /// <summary>
    /// The model at <paramref name="nowMs"/> (the loop's monotonic clock). Gives back <paramref name="previous"/> itself when nothing changed, so the window can skip the redraw:
    /// same brackets at the same whole pixels and the same panel instance. Brackets are placed on whole pixels.
    /// An empty snapshot, or one that nothing has refreshed for <see cref="LiveSettings.StaleHideMs"/>, gives <see cref="OverlayModel.Empty"/>.
    /// </summary>
    public static OverlayModel ModelAt(LiveSnapshot snapshot, double nowMs, LiveSettings settings, OverlayModel? previous = null)
    {
        if (snapshot.Brackets.Count == 0 || nowMs - snapshot.BuiltMs > settings.StaleHideMs)
            return OverlayModel.Empty;

        var items = new List<BracketItem>(snapshot.Brackets.Count);
        foreach (var bracket in snapshot.Brackets)
        {
            var (dx, dy) = Shift(bracket, nowMs, settings);
            var box = bracket.Box;
            items.Add(new BracketItem(new PixelRect(Math.Round(box.X + dx), Math.Round(box.Y + dy), box.Width, box.Height), bracket.Kind, bracket.Color));
        }

        if (previous is not null && ReferenceEquals(previous.Panel, snapshot.Panel) && previous.Brackets.SequenceEqual(items))
            return previous;
        return new OverlayModel(items, snapshot.Panel);
    }

    /// <summary>
    /// How far the bracket has moved from its last tracked position at <paramref name="nowMs"/>, in pixels: velocity times the time since that position, the
    /// time capped at <see cref="LiveSettings.RenderExtrapolationMs"/> and the distance at <see cref="LiveSettings.MaxShiftBoxHeights"/> box heights.
    /// </summary>
    public static (double X, double Y) Shift(LiveBracket bracket, double nowMs, LiveSettings settings)
    {
        var dt = Math.Min(Math.Max(0.0, nowMs - bracket.ReferenceMs), settings.RenderExtrapolationMs);
        var speed = Math.Sqrt(bracket.VxPerMs * bracket.VxPerMs + bracket.VyPerMs * bracket.VyPerMs);
        if (dt <= 0 || speed < settings.MinSpeedPxPerMs)
            return (0.0, 0.0);

        var distance = speed * dt;
        var limit = settings.MaxShiftBoxHeights * bracket.Box.Height;
        var scale = distance > limit && distance > 0 ? limit / distance : 1.0;
        return (bracket.VxPerMs * dt * scale, bracket.VyPerMs * dt * scale);
    }
}
