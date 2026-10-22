namespace Iris.Core.Overlay;

/// <summary>Where the game window stands, for the overlay.</summary>
public enum GameWindowState
{
    /// <summary>Star Citizen is not running, not in front, or minimized: no overlay.</summary>
    GameNotActive,

    /// <summary>Star Citizen is in front but is not a borderless window covering its monitor: no overlay (it would not line up).</summary>
    NotBorderless,

    /// <summary>Star Citizen is in front and borderless: the overlay may be shown over it.</summary>
    Ready,
}

/// <summary>
/// The overlay's rules about the game window, as pure functions over rectangles and window style flags
/// (the Windows calls that read them live in the app).
/// </summary>
public static class GameWindowRules
{
    /// <summary>WS_CAPTION (title bar and border): 0x00C00000.</summary>
    public const uint StyleCaption = 0x00C00000;

    /// <summary>WS_THICKFRAME (resizable frame): 0x00040000.</summary>
    public const uint StyleThickFrame = 0x00040000;

    /// <summary>
    /// Borderless = no caption bits, no resizable frame, and the window rectangle is exactly the monitor's rectangle.
    /// </summary>
    public static bool IsBorderless(PixelRect window, PixelRect monitor, uint style)
    {
        if ((style & StyleCaption) != 0 || (style & StyleThickFrame) != 0)
            return false;
        return !window.IsEmpty && window == monitor;
    }

    /// <summary>The state for the window in front: <paramref name="isStarCitizenInFront"/> false is always <see cref="GameWindowState.GameNotActive"/>.</summary>
    public static GameWindowState Decide(bool isStarCitizenInFront, PixelRect window, PixelRect monitor, uint style)
    {
        if (!isStarCitizenInFront)
            return GameWindowState.GameNotActive;
        return IsBorderless(window, monitor, style) ? GameWindowState.Ready : GameWindowState.NotBorderless;
    }
}

/// <summary>
/// Remembers for which game sessions (process ids) the "not in borderless mode" notice was already shown,
/// so the user hears it once per game session and not every time they alt-tab.
/// </summary>
public sealed class BorderlessNoticeTracker
{
    private readonly HashSet<int> _notified = new();

    /// <summary>True the first time it is asked for this process id; false afterwards.</summary>
    public bool ShouldNotify(int processId) => _notified.Add(processId);

    public const string Text =
        "Star Citizen is not in borderless windowed mode, so the overlay is turned off. Switch to Borderless in the game's graphics settings.";
}
