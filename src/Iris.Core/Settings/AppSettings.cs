namespace Iris.Core.Settings;

/// <summary>
/// The settings of this PC (file <c>Data\settings.toml</c>). Separate from the list file on purpose: these are never
/// shared or exported. A record, so "did anything change?" is a plain <c>==</c>, and a changed copy is <c>settings with { ... }</c>.
/// </summary>
public sealed record AppSettings
{
    public const int FormatVersion = 1;

    /// <summary>How old a cached RSI entry may get before it is refreshed: 30 (default) or 7 days.</summary>
    public int CacheTtlDays { get; init; } = 30;

    /// <summary>Write the daily log file in Data\logs. The in-memory log always works.</summary>
    public bool SessionLogEnabled { get; init; } = true;

    /// <summary>Check GitHub for new IRIS versions. Only a setting for now; the check itself comes later.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>The user's own web address of a shared config file (https only), or empty for none.</summary>
    public string HostedConfigUrl { get; init; } = string.Empty;

    /// <summary>SHA-256 (lower-case hex) of the last hosted file that was applied or declined, or empty.</summary>
    public string HostedConfigLastHash { get; init; } = string.Empty;

    /// <summary>
    /// Opt-in: look at the hosted config address in the background every 24 hours and offer the update when the file changed
    /// (see <see cref="Iris.Core.Config.HostedConfigSchedule"/>). Off by default. Only meaningful with a valid <see cref="HostedConfigUrl"/>.
    /// </summary>
    public bool HostedConfigAutoCheck { get; init; }

    /// <summary>When the last automatic check was made (UTC, whole seconds; success or failure), or null if there was none yet.</summary>
    public DateTimeOffset? HostedConfigLastCheck { get; init; }

    // ---- Overlay panel (Phase 2) ----

    public const int PanelWidthPercentMin = 8;
    public const int PanelWidthPercentMax = 40;
    public const int PanelTextSizeMin = 10;
    public const int PanelTextSizeMax = 40;
    public const int PanelRightGapPercentMin = 0;
    public const int PanelRightGapPercentMax = 40;
    public const int PanelTopGapPercentMin = 0;
    public const int PanelTopGapPercentMax = 60;

    /// <summary>Show the right-hand panel (one box per bracketed player). The brackets themselves are always shown.</summary>
    public bool PanelEnabled { get; init; } = true;

    /// <summary>Width of the panel as a percentage of the game window width (8 to 40).</summary>
    public int PanelWidthPercent { get; init; } = 16;

    /// <summary>Panel text size in pixels on a 1440p-high game window (10 to 40); it scales linearly with the real window height.</summary>
    public int PanelTextSize { get; init; } = 18;

    /// <summary>Space between the panel's right edge and the right edge of the screen, as a percentage of the width (0 to 40).</summary>
    public int PanelRightGapPercent { get; init; } = 10;

    /// <summary>Space between the top of the screen and the panel, as a percentage of the height (0 to 60).</summary>
    public int PanelTopGapPercent { get; init; } = 15;

    // ---- Live detection (Phase 3) ----

    /// <summary>The lowest tracking rate (a smaller number in the file becomes this one).</summary>
    public const int NameplateTrackingFpsMin = 30;

    /// <summary>The highest tracking rate that is a number; one step above it is "Max".</summary>
    public const int NameplateTrackingFpsHighest = 90;

    /// <summary>
    /// The value of <see cref="NameplateTrackingFps"/> that stands for "Max" (every new frame of the game, no cap): the last stop of the Settings tab slider, one step
    /// above <see cref="NameplateTrackingFpsHighest"/>. It is written to the file as the text "max".
    /// </summary>
    public const int NameplateTrackingFpsMax = NameplateTrackingFpsHighest + NameplateTrackingFpsStep;

    public const int NameplateTrackingFpsStep = 5;
    public const int NameplateTrackingFpsDefault = 60;

    /// <summary>
    /// "Nameplate Tracking Tickrate": the most times a second the nameplates are TRACKED, 30 to 90 in steps of 5, or <see cref="NameplateTrackingFpsMax"/> for "Max"
    /// (the file says <c>"max"</c>; a number above 90 in an old file means Max, too). The detector (the OCR) reads the screen only 5 times a second (not a setting) and
    /// decides what is on screen and what it is called; in between, every NEW frame of the game that is at least 1000 / this many milliseconds after the last tracked one
    /// is tracked: IRIS copies a small patch of the screen around each known nameplate and finds the nameplate's text in it again (template matching), which moves
    /// the bracket. Max tracks every new frame (the game's own frame rate, no other cap). Tracking never reads or changes names. A higher number follows fast turns
    /// more closely and costs more screen copies and processor time (the log's minute line has the real numbers); the brackets themselves are drawn at the monitor's
    /// refresh rate regardless.
    /// </summary>
    public int NameplateTrackingFps { get; init; } = NameplateTrackingFpsDefault;

    /// <summary>True when the tracking rate is "Max": every new frame is tracked.</summary>
    public bool IsTrackingMax => NameplateTrackingFps > NameplateTrackingFpsHighest;

    /// <summary>The cap for <see cref="Iris.Core.Live.TrackingCap"/>: tracked frames a second, or 0 for Max (no cap).</summary>
    public int TrackingCapFps => IsTrackingMax ? 0 : Math.Max(NameplateTrackingFpsMin, NameplateTrackingFps);

    /// <summary>
    /// "Show overlay in screenshots and recordings" (<c>overlay_visible_in_capture</c>, off by default). Off: the overlay windows are left out of every screen capture
    /// (Windows' WDA_EXCLUDEFROMCAPTURE), so recorders, OBS, Discord and screenshots do not show them. On: the windows are visible to recorders (display affinity
    /// none) and IRIS blacks out its own brackets and panel in the pictures it reads itself. Takes effect at once for all overlay windows, no restart.
    /// </summary>
    public bool OverlayVisibleInCapture { get; init; }

    // ---- Disabling the overlay, hotkeys ----

    /// <summary>
    /// "Disable Overlay": hides the whole overlay AND stops all screen reading (OCR). Set by the tray item or the hotkey, kept across restarts.
    /// The panel has no matching setting of its own: "Disable Info Panel" is <see cref="PanelEnabled"/> = false.
    /// </summary>
    public bool OverlayDisabled { get; init; }

    /// <summary>Global hotkey that toggles "Disable Overlay", like "Ctrl+Shift+O" (see <see cref="HotkeyText"/>), or empty for none.</summary>
    public string HotkeyToggleOverlay { get; init; } = string.Empty;

    /// <summary>Global hotkey that toggles "Disable Info Panel", or empty for none.</summary>
    public string HotkeyTogglePanel { get; init; } = string.Empty;
}
