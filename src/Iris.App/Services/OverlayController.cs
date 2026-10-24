using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Settings;
using Iris.Core.Social;

namespace Iris.App.Services;

/// <summary>
/// Whether screen capture and reading (OCR) may run right now, and over which rectangle: <see cref="Active"/> only while Star Citizen is in front, borderless
/// (<see cref="GameWindowState.Ready"/>), the overlay is not disabled and no test overlay is running. <see cref="Bounds"/> is the game window in screen pixels.
/// The detection loop (<see cref="LiveDetectionService"/>) follows it; nothing is captured while it is <see cref="Off"/>.
/// </summary>
public sealed record CaptureGate(bool Active, PixelRect Bounds)
{
    public static readonly CaptureGate Off = new(false, default);
}

/// <summary>
/// Decides when the overlay window is shown and what it shows. Two sources feed it:
/// - the live snapshot (<see cref="AttachLive"/>; the capture loop keeps the brackets and panel of the confirmed nameplates in a <see cref="LiveSnapshotHub"/>, tracked 15 to 120 times
///   a second), shown only while Star Citizen is in front and borderless (<see cref="GameWindowTracker"/>), the overlay is not disabled, and there is something to draw.
///   It is drawn by two small windows, one for the brackets and one for the panel and the confirmation text (see <see cref="OverlayWindow"/>). The brackets window renders at
///   monitor refresh by itself (it asks <see cref="RenderModel"/> for the model of every frame); this class only decides which windows are shown, when the live snapshot
///   changed in a way that matters for that (<see cref="LiveChanged"/>: brackets appeared or all went, a new panel);
/// - the test overlay (<see cref="StartTest"/>), shown for 15 seconds whatever is in front, in one window of the whole size.
/// "Disable Overlay" (<see cref="IsOverlayDisabled"/>) and "Disable Info Panel" (the <c>panel_enabled</c> setting) are toggled from the tray
/// (<see cref="ToggleFromTray"/>) and from the global hotkeys (<see cref="Hotkeys"/>); the hotkeys also show a short on-screen
/// confirmation (just above the panel) while the game is in front and borderless, even when the overlay is disabled (then only that text is drawn).
/// The windows are created the first time they are needed and afterwards only hidden. While the game is not in front the only work is the
/// tracker's 250 ms foreground check. <see cref="Gate"/> tells the detection loop when it may capture.
/// </summary>
public sealed class OverlayController : IDisposable
{
    public const int TestSeconds = 15;
    public const double ToastSeconds = 2;

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly GameWindowTracker _tracker;
    private readonly BorderlessNoticeTracker _notice = new();
    private readonly DispatcherTimer _testTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private OverlayWindow? _testWindow;
    private OverlayWindow? _bracketWindow;
    private OverlayWindow? _panelWindow;
    private OverlayModel _liveModel = OverlayModel.Empty;   // the model of the last frame (or of the last Evaluate); UI thread
    private LiveSnapshotHub? _hub;
    private LiveSettings _liveSettings = LiveSettings.Default;
    private bool _evaluatePending;
    private volatile CaptureGate _gate = CaptureGate.Off; // read by the detection thread
    private bool _disposed;

    // the on-screen confirmation ("Overlay off"), shown for ToastSeconds over the game
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(ToastSeconds) };
    private string? _toastText;

    // the running test overlay (null model = none)
    private OverlayModel? _testModel;
    private PixelRect _testBounds;
    private AppSettings? _testSettings;
    private long _testEndTick;

    public OverlayController(AppServices services, ImportCoordinator imports)
    {
        _services = services;
        _imports = imports;
        _tracker = new GameWindowTracker(services.Log, services.Clock);
        _tracker.Changed += OnGameWindowChanged;
        _tracker.Checked += OnGameWindowChecked;
        _testTimer.Tick += OnTestTick;
        _toastTimer.Tick += OnToastTick;
        services.SettingsApplied += OnSettingsApplied;
        Hotkeys = new HotkeyManager(services.Log, services.Clock);
        Hotkeys.Pressed += OnHotkeyPressed;
    }

    /// <summary>The global hotkeys (the Settings tab shows their errors).</summary>
    public HotkeyManager Hotkeys { get; }

    /// <summary>Raised (on the UI thread) when the test overlay starts, ends, or its countdown moves on.</summary>
    public event EventHandler? TestStateChanged;

    /// <summary>
    /// True while "Disable Overlay" is on: nothing is drawn and no screen capture or reading (OCR) happens: <see cref="Gate"/> is off then, and the detection loop
    /// follows it; only the 250 ms foreground check keeps running.
    /// </summary>
    public bool IsOverlayDisabled => _services.IsOverlayDisabled;

    /// <summary>Raised when <see cref="IsOverlayDisabled"/> changed.</summary>
    public event EventHandler OverlayDisabledChanged
    {
        add => _services.OverlayDisabledChanged += value;
        remove => _services.OverlayDisabledChanged -= value;
    }

    public bool IsTestRunning => _testModel is not null;

    /// <summary>Whole seconds left of the running test overlay (0 when none runs).</summary>
    public int TestSecondsLeft
    {
        get
        {
            if (_testModel is null)
                return 0;
            var left = _testEndTick - Environment.TickCount64;
            return left <= 0 ? 0 : (int)((left + 999) / 1000);
        }
    }

    /// <summary>
    /// Whether the detection loop may capture and read the screen now, and where the game window is. Changes are announced by <see cref="GateChanged"/>
    /// (on the UI thread). The loop reads this from its own thread: the value is an immutable record in a volatile field.
    /// </summary>
    public CaptureGate Gate => _gate;

    /// <summary>Raised on the UI thread when <see cref="Gate"/> changed (the game came to the front or left, the overlay was disabled or enabled, the window moved).</summary>
    public event EventHandler? GateChanged;

    /// <summary>Starts watching the game window.</summary>
    public void Start()
    {
        Hotkeys.Apply(_services.Settings);
        _tracker.Start();
    }

    /// <summary>
    /// Connects the overlay to the capture loop's snapshots (<paramref name="hub"/>): from now on the brackets window renders them at monitor refresh. Call it on the UI thread,
    /// once, before the capture loop starts.
    /// </summary>
    public void AttachLive(LiveSnapshotHub hub, LiveSettings live)
    {
        _hub = hub;
        _liveSettings = live;
    }

    /// <summary>
    /// The live snapshot changed in a way the windows must hear about (the capture loop calls it, through the UI thread, only then): brackets appeared or all of them
    /// went, or the panel's entries changed. The movement of the brackets is NOT announced: the brackets window hooks the monitor refresh and finds out by itself.
    /// </summary>
    public void LiveChanged() => Evaluate();

    /// <summary>
    /// Writes the brackets window's "Overlay render" log line (frames, redraws, average cost since the last call). The live detection service calls it (through the UI thread)
    /// right after its once-a-minute "Live detection" line: that is the only time the line is written. Nothing is written when no frame was rendered since.
    /// </summary>
    public void LogRenderSummary() => _bracketWindow?.LogRenderSummary();

    /// <summary>
    /// The model for the moment the frame will be SHOWN from the latest snapshot: the brackets moved along their tracked velocity from where they were last tracked to the
    /// expected display time (now plus one refresh period of the display, <see cref="DisplayTiming"/>), at most 50 ms ahead of their last tracked time.
    /// </summary>
    private OverlayModel ModelAtNow(OverlayModel? previous)
    {
        var hub = _hub;
        return hub is null
            ? OverlayModel.Empty
            : LivePredictor.ModelAt(hub.Current, StopwatchClock.Shared.NowMs + DisplayTiming.RefreshPeriodMs(_tracker.Current.Handle), _liveSettings, previous);
    }

    /// <summary>
    /// The brackets window's frame callback (monitor refresh, UI thread): the model of this frame, the same instance as <paramref name="shown"/> when no bracket moved by
    /// a whole pixel. When the snapshot has run out (no brackets left, or stale) the windows are evaluated again so that the panel goes too.
    /// </summary>
    private OverlayModel RenderModel(OverlayModel shown)
    {
        var model = ModelAtNow(shown);
        if (ReferenceEquals(model, _liveModel))
            return model;
        var emptied = model.Brackets.Count == 0 && _liveModel.Brackets.Count > 0;
        _liveModel = model;
        if (emptied && !_evaluatePending)
        {
            _evaluatePending = true;
            _testTimer.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _evaluatePending = false;
                Evaluate();
            }));
        }

        return model;
    }

    // ---------------------------------------------------------------------------------------
    // Disable Overlay / Disable Info Panel
    // ---------------------------------------------------------------------------------------

    /// <summary>A tray item was used: flips the setting (no on-screen text).</summary>
    public void ToggleFromTray(HotkeyAction action) => Toggle(action, announce: false);

    private void OnHotkeyPressed(object? sender, HotkeyAction action) => Toggle(action, announce: true);

    /// <summary>
    /// Flips "Disable Overlay" or "Disable Info Panel" and saves that one setting. With <paramref name="announce"/> the result is also shown:
    /// on screen for ~2 s when Star Citizen is in front and borderless (so the overlay can be drawn; also while the overlay is disabled, then only
    /// the text is drawn), otherwise as a tray balloon. While the test overlay runs it covers the screen instead of the live overlay, so the
    /// result goes to the tray balloon then. A confirmation from an earlier toggle is replaced.
    /// </summary>
    private void Toggle(HotkeyAction action, bool announce)
    {
        // an earlier confirmation ends here; its timer must not cut the new one short (the redraw follows at the end of this method)
        _toastText = null;
        _toastTimer.Stop();

        var settings = _services.Settings;
        string text;
        var onScreen = announce && _testModel is null && _tracker.Current.State == GameWindowState.Ready;

        // The text is set before the setting changes, so the single redraw that the change causes already contains it.
        if (action == HotkeyAction.ToggleOverlay)
        {
            var disable = !settings.OverlayDisabled;
            text = disable ? "Overlay off" : "Overlay on";
            if (onScreen)
                ShowToast(text);
            var saved = _services.SetOverlayDisabled(disable);
            if (!saved.Success)
            {
                ClearToast();
                _services.Notify?.Invoke("Could not save the setting: " + saved.Error);
                return;
            }
        }
        else
        {
            var enable = !settings.PanelEnabled;
            text = enable ? "Info panel on" : "Info panel off";
            if (onScreen)
                ShowToast(text); // also while the overlay is disabled: Evaluate then draws just this text
            var saved = _services.SetPanelEnabled(enable);
            if (!saved.Success)
            {
                ClearToast();
                _services.Notify?.Invoke("Could not save the setting: " + saved.Error);
                return;
            }
        }

        if (announce && _toastText is null)
            _services.Notify?.Invoke(text);
        Evaluate();
    }

    private void ShowToast(string text)
    {
        _toastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ClearToast()
    {
        _toastText = null;
        _toastTimer.Stop();
        Evaluate();
    }

    private void OnToastTick(object? sender, EventArgs e) => ClearToast();

    // ---------------------------------------------------------------------------------------
    // Test overlay
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the sample overlay for 15 seconds, built from the saved lists, over the Star Citizen window if the game is running (even when it is
    /// not in front), otherwise over the monitor <paramref name="anchor"/> is on. The sizes follow <paramref name="settings"/>, which may be
    /// unsaved values from the Settings tab; the panel itself follows the saved panel switch (the tray state). Returns a short phrase saying where it is shown ("over the Star Citizen window").
    /// </summary>
    public string StartTest(AppSettings settings, Window anchor)
    {
        string where;
        if (_tracker.TryFindGameWindow(out var gameBounds))
        {
            _testBounds = gameBounds;
            where = "over the Star Citizen window";
        }
        else
        {
            var handle = new WindowInteropHelper(anchor).Handle;
            if (!NativeMethods.TryGetMonitorBounds(handle, out var monitorBounds))
                throw new InvalidOperationException("Windows did not report the size of the screen.");
            _testBounds = monitorBounds;
            where = "over this screen (Star Citizen is not running)";
        }

        _testModel = TestOverlayBuilder.Build(_services.CurrentConfig, LookupCachedOrgs, _testBounds.Width, _testBounds.Height);
        _testSettings = settings;
        _testEndTick = Environment.TickCount64 + TestSeconds * 1000L;
        _testTimer.Start();
        _services.Log.Write(_services.Clock.UtcNow, $"Test overlay started {where} ({_testBounds.Width}x{_testBounds.Height})");

        Evaluate();
        TestStateChanged?.Invoke(this, EventArgs.Empty);
        return where;
    }

    /// <summary>Ends the test overlay (also called when its 15 seconds are over).</summary>
    public void StopTest()
    {
        if (_testModel is null)
            return;
        _testModel = null;
        _testSettings = null;
        _testTimer.Stop();
        _services.Log.Write(_services.Clock.UtcNow, "Test overlay ended");

        Evaluate();
        TestStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Settings tab changed a value while the test overlay runs: show the panel with the new values at once.</summary>
    public void UpdateTestSettings(AppSettings settings)
    {
        if (_testModel is null)
            return;
        _testSettings = settings;
        Evaluate();
    }

    private IReadOnlyList<OrgMembership>? LookupCachedOrgs(string handle)
    {
        try
        {
            return _services.Database.GetPlayer(handle)?.Orgs;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return null; // a cache problem must not stop the test overlay: the player simply shows as "not looked up"
        }
    }

    private void OnTestTick(object? sender, EventArgs e)
    {
        if (TestSecondsLeft <= 0)
            StopTest();
        else
            TestStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Showing and hiding
    // ---------------------------------------------------------------------------------------

    private void OnGameWindowChanged(object? sender, GameWindowSnapshot snapshot) => Evaluate();

    // After EVERY check of the game window, not only when something changed: the notice must not depend on a transition (the game may
    // already have been in front and not borderless when IRIS started, and a check that had to say "not now" must be repeated).
    private void OnGameWindowChecked(object? sender, GameWindowSnapshot snapshot)
    {
        if (_disposed || _imports.IsQuitting)
            return;
        if (_notice.ShouldShow(snapshot.State, _services.IsOverlayDisabled, PromptDialog.IsAnyOpen, snapshot.Mode))
            ShowBorderlessNotice(snapshot.Mode);
    }

    /// <summary>
    /// The "not in borderless mode" pop-up, at most once per run of IRIS. Not shown while the overlay is disabled or another IRIS pop-up is open
    /// (the next check asks again). It is not an IRIS question: it is not counted by <see cref="ImportCoordinator.IsBusy"/>, so Settings and
    /// Quit work while it is open (Quit closes it, see <see cref="CloseBorderlessNotice"/>). It follows the pop-up policy: with the game in front
    /// it is not activated and waits in the taskbar; there is no tray balloon.
    /// </summary>
    private void ShowBorderlessNotice(WindowMode? gameMode)
    {
        _notice.MarkShown(); // first: this check must not open a second one, whatever happens below
        _services.Log.Write(_services.Clock.UtcNow, "Told the user that Star Citizen is not in borderless windowed mode (game display mode: " + (gameMode?.ToString() ?? "unknown") + ")");
        PromptDialog.ShowNotice(BorderlessNoticeTracker.TextFor(gameMode));
    }

    /// <summary>Closes the "not in borderless mode" pop-up if it is open (IRIS is quitting). Safe to call at any time.</summary>
    public void CloseBorderlessNotice() => PromptDialog.CloseNotice();

    private void OnSettingsApplied(object? sender, EventArgs e)
    {
        // settings are normally applied on the UI thread; if not, the window must still be touched on it
        var dispatcher = _testTimer.Dispatcher;
        if (dispatcher.CheckAccess())
            ApplySettingsChange();
        else
            dispatcher.BeginInvoke(new Action(ApplySettingsChange));
    }

    private void ApplySettingsChange()
    {
        if (_disposed)
            return;
        Hotkeys.Apply(_services.Settings);
        // "Show overlay in screenshots and recordings": every window changes over at once, shown or not
        var visibleInCapture = _services.Settings.OverlayVisibleInCapture;
        foreach (var window in new[] { _testWindow, _bracketWindow, _panelWindow })
            window?.SetVisibleInCapture(visibleInCapture);
        // the test overlay's panel follows the saved panel switch (tray state / hotkey), also when that was just flipped
        if (_testSettings is not null)
            _testSettings = _testSettings with { PanelEnabled = _services.Settings.PanelEnabled };
        Evaluate();
    }

    /// <summary>Looks at everything that decides visibility and shows or hides the windows. Cheap when nothing changed.</summary>
    private void Evaluate()
    {
        if (_disposed)
            return;

        if (_testModel is not null && _testSettings is not null)
        {
            // the test overlay covers the screen instead of the live one, and nothing may be captured meanwhile
            SetGate(CaptureGate.Off);
            _bracketWindow?.HideOverlay();
            _panelWindow?.HideOverlay();
            EnsureTestWindow().Present(_testModel, _testBounds, _testSettings);
            return;
        }

        _testWindow?.HideOverlay();

        _liveModel = ModelAtNow(_liveModel); // the snapshot may have changed since the last frame
        var snapshot = _tracker.Current;
        var settings = _services.Settings;
        var ready = snapshot.State == GameWindowState.Ready;
        // capture and OCR only while the overlay could be shown: Disable Overlay stops them entirely
        SetGate(ready && !settings.OverlayDisabled ? new CaptureGate(true, snapshot.Bounds) : CaptureGate.Off);

        if (!ready)
        {
            HideLive();
            return;
        }

        if (settings.OverlayDisabled)
        {
            // everything is off, except the confirmation "Overlay off" for its two seconds
            _bracketWindow?.HideOverlay();
            if (_toastText is null)
                _panelWindow?.HideOverlay();
            else
                EnsurePanelWindow().Present(OverlayModel.Empty, snapshot.Bounds, settings, _toastText);
            return;
        }

        if (_liveModel.Brackets.Count > 0)
            EnsureBracketWindow().Present(_liveModel, snapshot.Bounds, settings);
        else
            _bracketWindow?.HideOverlay();

        if (_toastText is not null || (settings.PanelEnabled && _liveModel.Panel.Count > 0))
            EnsurePanelWindow().Present(_liveModel, snapshot.Bounds, settings, _toastText);
        else
            _panelWindow?.HideOverlay();
    }

    private void SetGate(CaptureGate gate)
    {
        if (gate == _gate)
            return;
        _gate = gate;
        GateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideLive()
    {
        _bracketWindow?.HideOverlay();
        _panelWindow?.HideOverlay();
    }

    private void WarnWindow(string text) => _services.Log.Write(_services.Clock.UtcNow, text);

    private OverlayWindow EnsureTestWindow() => _testWindow ??= new OverlayWindow(OverlayLayer.All, WarnWindow, _services.Settings.OverlayVisibleInCapture);

    private OverlayWindow EnsureBracketWindow() =>
        _bracketWindow ??= new OverlayWindow(OverlayLayer.Brackets, WarnWindow, _services.Settings.OverlayVisibleInCapture) { ModelProvider = RenderModel };

    private OverlayWindow EnsurePanelWindow() => _panelWindow ??= new OverlayWindow(OverlayLayer.Panel, WarnWindow, _services.Settings.OverlayVisibleInCapture);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _services.SettingsApplied -= OnSettingsApplied;
        Hotkeys.Pressed -= OnHotkeyPressed;
        Hotkeys.Dispose();
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTick;
        _tracker.Changed -= OnGameWindowChanged;
        _tracker.Checked -= OnGameWindowChecked;
        PromptDialog.CloseNotice();
        _tracker.Dispose();
        _testTimer.Stop();
        _testTimer.Tick -= OnTestTick;
        foreach (var window in new[] { _testWindow, _bracketWindow, _panelWindow })
            window?.Close(); // destroys the Win32 window and its bitmap
        _testWindow = null;
        _bracketWindow = null;
        _panelWindow = null;
        RenderKeepAlive.Shutdown();
    }
}
