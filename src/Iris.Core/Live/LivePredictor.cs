using Iris.Core.Overlay;

namespace Iris.Core.Live;

/// <summary>
/// The "tracking tick": the overlay model for a moment between two reads. Every bracket of the snapshot is moved from the place where its plate was last seen along
/// the speed the plate was seen moving at (constant velocity), for the time since it was seen, at most <see cref="LiveSettings.MaxPredictionMs"/>.
/// This is arithmetic on a handful of numbers: no capture, no OCR, no allocation worth mentioning, so it can run 15 to 120 times a second. It is a
/// prediction, not a measurement: a plate that changes direction or speed between two reads is wrong by what it moved differently, and the next read puts the bracket right.
/// </summary>
public static class LivePredictor
{
    /// <summary>
    /// The model at <paramref name="nowMs"/> (the loop's monotonic clock). Gives back <paramref name="previous"/> itself when nothing changed, so the window can skip the redraw:
    /// same brackets at the same whole pixels and the same panel instance. Brackets are placed on whole pixels.
    /// An empty snapshot, or one that no read has refreshed for <see cref="LiveSettings.StaleHideMs"/>, gives <see cref="OverlayModel.Empty"/>.
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

    /// <summary>How far the bracket has moved from where its plate was last seen at <paramref name="nowMs"/>, in pixels.</summary>
    public static (double X, double Y) Shift(LiveBracket bracket, double nowMs, LiveSettings settings)
    {
        var dt = Math.Min(Math.Max(0.0, nowMs - bracket.ReferenceMs), settings.MaxPredictionMs);
        var speed = Math.Sqrt(bracket.VxPerMs * bracket.VxPerMs + bracket.VyPerMs * bracket.VyPerMs);
        if (dt <= 0 || speed < settings.MinSpeedPxPerMs)
            return (0.0, 0.0);

        var distance = speed * dt;
        var limit = settings.MaxShiftBoxHeights * bracket.Box.Height;
        var scale = distance > limit && distance > 0 ? limit / distance : 1.0;
        return (bracket.VxPerMs * dt * scale, bracket.VyPerMs * dt * scale);
    }
}
