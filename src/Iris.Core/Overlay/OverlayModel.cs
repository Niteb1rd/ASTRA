using Iris.Core.Config;
using Iris.Core.Social;

namespace Iris.Core.Overlay;

/// <summary>A nameplate seen on screen: the handle read from it and its box (name + distance lines) in game-window pixels.</summary>
public sealed record DetectedPlayer(string Handle, PixelRect Box);

/// <summary>One bracket pair to draw around a nameplate box (game-window pixels). The shape follows <see cref="Kind"/>.</summary>
public sealed record BracketItem(PixelRect Box, MatchKind Kind, OverlayColor Color);

/// <summary>One player in the right-hand panel: the handle on line 1, <see cref="OrgsLine"/> on line 2.</summary>
public sealed record PanelEntry(string Handle, MatchKind Kind, OverlayColor Color, string OrgsLine);

/// <summary>
/// Everything the overlay window draws, already decided: the brackets and the panel entries (in panel order).
/// Phase 2 fills it with test data; Phase 3 will fill it with real detections through <see cref="OverlayModelBuilder"/>.
/// Treated as immutable: a new model is made whenever something changes, so the window can tell "changed" by reference.
/// </summary>
public sealed class OverlayModel
{
    public static readonly OverlayModel Empty = new(Array.Empty<BracketItem>(), Array.Empty<PanelEntry>());

    public OverlayModel(IReadOnlyList<BracketItem> brackets, IReadOnlyList<PanelEntry> panel)
    {
        Brackets = brackets;
        Panel = panel;
    }

    public IReadOnlyList<BracketItem> Brackets { get; }

    /// <summary>Panel entries in display order (see <see cref="OverlayModelBuilder"/>).</summary>
    public IReadOnlyList<PanelEntry> Panel { get; }

    public bool IsEmpty => Brackets.Count == 0 && Panel.Count == 0;
}
