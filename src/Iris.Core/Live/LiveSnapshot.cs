using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Social;

namespace Iris.Core.Live;

/// <summary>
/// One bracket as of the last read: the box (name and distance lines, game-window pixels) where the plate was last seen, at <see cref="ReferenceMs"/>,
/// and the speed it was seen moving at (pixels per millisecond), which <see cref="LivePredictor"/> uses to move the bracket between reads.
/// </summary>
public sealed record LiveBracket(
    int TrackId, string Handle, PixelRect Box, double VxPerMs, double VyPerMs, double ReferenceMs, MatchKind Kind, OverlayColor Color);

/// <summary>
/// Everything the last read decided, immutable (the detection thread builds it and hands it over; the tracking ticks only read it): the brackets of the
/// confirmed nameplates with their motion, and the panel entries. <see cref="Panel"/> keeps its INSTANCE as long as its content is the same, so the
/// overlay can tell "the panel did not change" by reference.
/// </summary>
public sealed class LiveSnapshot
{
    public static readonly LiveSnapshot Empty = new(Array.Empty<LiveBracket>(), Array.Empty<PanelEntry>(), double.NegativeInfinity);

    public LiveSnapshot(IReadOnlyList<LiveBracket> brackets, IReadOnlyList<PanelEntry> panel, double builtMs)
    {
        Brackets = brackets;
        Panel = panel;
        BuiltMs = builtMs;
    }

    public IReadOnlyList<LiveBracket> Brackets { get; }

    /// <summary>Panel entries in display order (<see cref="OverlayModelBuilder"/>).</summary>
    public IReadOnlyList<PanelEntry> Panel { get; }

    /// <summary>The time (the loop's monotonic clock) of the read that made this snapshot.</summary>
    public double BuiltMs { get; }
}
