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
/// Remembers whether the "not in borderless mode" notice was already shown during this run of IRIS. It is shown at most once per run
/// (not once per game session). It is only counted as shown when <see cref="MarkShown"/> is called, so a moment in which the notice
/// could not be shown (another question is open) does not use it up.
/// </summary>
public sealed class BorderlessNoticeTracker
{
    /// <summary>True when the notice was shown already during this run.</summary>
    public bool WasShown { get; private set; }

    /// <summary>True as long as the notice has not been shown during this run.</summary>
    public bool ShouldNotify() => !WasShown;

    /// <summary>
    /// The decision, made on EVERY check of the game window (not only when the state changes): show the notice when the game in front is not
    /// borderless, the notice has not been shown during this run, the overlay is not disabled (the user switched it off on purpose) and no other
    /// IRIS pop-up is open. A check that says no uses nothing up, so the next check asks again; this is what makes a game that was already
    /// running (and already not borderless) when IRIS started get its notice too.
    /// </summary>
    public bool ShouldShow(GameWindowState state, bool overlayDisabled, bool otherPopUpOpen) =>
        state == GameWindowState.NotBorderless && !WasShown && !overlayDisabled && !otherPopUpOpen;

    /// <summary>Records that the notice is being shown now; <see cref="ShouldNotify"/> is false from here on.</summary>
    public void MarkShown() => WasShown = true;

    public const string Text =
        "Star Citizen is not in borderless windowed mode, so the IRIS overlay is turned off. Switch to Borderless in the game's graphics settings.";
}
