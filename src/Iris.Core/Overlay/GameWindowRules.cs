namespace Iris.Core.Overlay;

/// <summary>Where the game window stands, for the overlay.</summary>
public enum GameWindowState
{
    /// <summary>Star Citizen is not running, not in front, or minimized: no overlay.</summary>
    GameNotActive,

    /// <summary>Star Citizen is in front but its Display Mode is Windowed or Fullscreen: no overlay.</summary>
    NotBorderless,

    /// <summary>Star Citizen is in front and its Display Mode is Borderless Windowed: the overlay may be shown over it.</summary>
    Ready,
}

/// <summary>
/// The overlay's rule about the game window. The game's Display Mode comes ONLY from its own attributes.xml (see
/// <see cref="StarCitizenDisplaySettings"/>); the window's shape and style are not looked at (an exclusive Fullscreen window looks exactly like
/// a borderless one, and a borderless game can be smaller than its monitor for a moment while it loads).
/// </summary>
public static class GameWindowRules
{
    /// <summary>
    /// The state for the window in front: <paramref name="isStarCitizenInFront"/> false is always <see cref="GameWindowState.GameNotActive"/>.
    /// Borderless is Ready; Windowed and Fullscreen are NotBorderless. Null means the mode is not settled yet (the first read failed and the
    /// second is due a second later): no overlay and no notice yet, so it counts as <see cref="GameWindowState.GameNotActive"/>.
    /// </summary>
    public static GameWindowState Decide(bool isStarCitizenInFront, WindowMode? gameMode)
    {
        if (!isStarCitizenInFront || gameMode is null)
            return GameWindowState.GameNotActive;
        return gameMode == WindowMode.Borderless ? GameWindowState.Ready : GameWindowState.NotBorderless;
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
    /// Never when the game's own setting says Borderless: then the window is only not filling the monitor YET (the game starts in a smaller window
    /// while it loads, or is being restored), and telling the user to switch to Borderless would be wrong.
    /// </summary>
    public bool ShouldShow(GameWindowState state, bool overlayDisabled, bool otherPopUpOpen, WindowMode? gameMode = null) =>
        state == GameWindowState.NotBorderless && gameMode != WindowMode.Borderless && !WasShown && !overlayDisabled && !otherPopUpOpen;

    /// <summary>Records that the notice is being shown now; <see cref="ShouldNotify"/> is false from here on.</summary>
    public void MarkShown() => WasShown = true;

    public const string Text =
        "Star Citizen is not in borderless windowed mode, so the IRIS overlay is turned off. Switch to Borderless in the game's graphics settings.";

    /// <summary>The notice text for the game's mode when it is known to be Fullscreen or Windowed; otherwise (unknown, or Borderless that does not fill the monitor) <see cref="Text"/>.</summary>
    public static string TextFor(WindowMode? gameMode) => gameMode switch
    {
        WindowMode.Fullscreen => "Star Citizen is in Fullscreen mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings.",
        WindowMode.Windowed => "Star Citizen is in Windowed mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings.",
        _ => Text,
    };
}
