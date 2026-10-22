using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core.Overlay;
using Iris.Core.Settings;
using Iris.Core.Social;

namespace Iris.App.Services;

/// <summary>
/// Decides when the overlay window is shown and what it shows. Two sources feed it:
/// - the live model (<see cref="SetLiveModel"/>; Phase 3 will put real detections here), shown only while Star Citizen is in front and
///   borderless (<see cref="GameWindowTracker"/>), the overlay is not disabled, and there is something to draw;
/// - the test overlay (<see cref="StartTest"/>), shown for 15 seconds whatever is in front.
/// "Disable Overlay" (<see cref="IsOverlayDisabled"/>) and "Disable Info Panel" (the <c>panel_enabled</c> setting) are toggled from the tray and the
/// Settings tab (<see cref="ToggleFromTray"/>) and from the global hotkeys (<see cref="Hotkeys"/>); the hotkeys also show a short on-screen
/// confirmation (just above the panel) while the game is in front and borderless, even when the overlay is disabled (then only that text is drawn).
/// The window is created the first time it is needed and afterwards only hidden. While the game is not in front the only work is the
/// tracker's 250 ms foreground check.
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

    private OverlayWindow? _window;
    private OverlayModel _liveModel = OverlayModel.Empty;
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
    /// True while "Disable Overlay" is on: nothing is drawn and no screen reading may happen. The future capture loop checks this (or listens to
    /// <see cref="OverlayDisabledChanged"/>); only the 250 ms foreground check keeps running.
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

    /// <summary>Starts watching the game window.</summary>
    public void Start()
    {
        Hotkeys.Apply(_services.Settings);
        _tracker.Start();
    }

    /// <summary>Gives the overlay the detections to show while the game is in front (Phase 3). The empty model hides the overlay.</summary>
    public void SetLiveModel(OverlayModel model)
    {
        _liveModel = model;
        Evaluate();
    }

    // ---------------------------------------------------------------------------------------
    // Disable Overlay / Disable Info Panel
    // ---------------------------------------------------------------------------------------

    /// <summary>A tray item or the Settings tab's "Disable Overlay" checkbox was used: flips the setting (no on-screen text).</summary>
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
    /// not in front), otherwise over the monitor <paramref name="anchor"/> is on. The panel follows <paramref name="settings"/>, which may be
    /// unsaved values from the Settings tab. Returns a short phrase saying where it is shown ("over the Star Citizen window").
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

    private void OnGameWindowChanged(object? sender, GameWindowSnapshot snapshot)
    {
        Evaluate();

        // No notice while the overlay is disabled (the user switched it off on purpose); it is not used up then, so it can still come later.
        if (snapshot.State == GameWindowState.NotBorderless && !_services.IsOverlayDisabled && _notice.ShouldNotify())
            ShowBorderlessNotice();
    }

    /// <summary>
    /// The "not in borderless mode" pop-up, at most once per run of IRIS. Skipped (and tried again at the next state change) while another IRIS
    /// question or pop-up is open. It follows the pop-up policy: with the game in front it is not activated and waits in the taskbar; there is
    /// no tray balloon.
    /// </summary>
    private void ShowBorderlessNotice()
    {
        if (_disposed || _imports.IsQuitting || PromptDialog.IsAnyOpen)
            return;
        var host = _imports.Host;
        if (host is not null && !host.IsIdleForPrompt)
            return;
        if (!_imports.TryBeginQuestion())
            return; // an import or another question is open

        try
        {
            _notice.MarkShown(); // before the dialog opens: its modal loop keeps the timers running and must not open a second one
            _services.Log.Write(_services.Clock.UtcNow, "Told the user that Star Citizen is not in borderless windowed mode");
            PromptDialog.TellWithClose(BorderlessNoticeTracker.Text, host?.DialogOwner);
        }
        finally
        {
            _imports.EndQuestion();
        }
    }

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
        Evaluate();
    }

    /// <summary>Looks at everything that decides visibility and shows or hides the window. Cheap when nothing changed.</summary>
    private void Evaluate()
    {
        if (_disposed)
            return;

        if (_testModel is not null && _testSettings is not null)
        {
            EnsureWindow().Present(_testModel, _testBounds, _testSettings);
            return;
        }

        var snapshot = _tracker.Current;
        var settings = _services.Settings;
        if (snapshot.State != GameWindowState.Ready)
        {
            _window?.HideOverlay();
            return;
        }

        if (settings.OverlayDisabled)
        {
            // everything is off, except the confirmation "Overlay off" for its two seconds
            if (_toastText is null)
                _window?.HideOverlay();
            else
                EnsureWindow().Present(OverlayModel.Empty, snapshot.Bounds, settings, _toastText);
            return;
        }

        if (_toastText is null && !HasSomethingToDraw(_liveModel, settings))
        {
            _window?.HideOverlay();
            return;
        }

        EnsureWindow().Present(_liveModel, snapshot.Bounds, settings, _toastText);
    }

    private static bool HasSomethingToDraw(OverlayModel model, AppSettings settings) =>
        model.Brackets.Count > 0 || (settings.PanelEnabled && model.Panel.Count > 0);

    private OverlayWindow EnsureWindow() => _window ??= new OverlayWindow();

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
        _tracker.Dispose();
        _testTimer.Stop();
        _testTimer.Tick -= OnTestTick;
        if (_window is not null)
        {
            _window.Close();
            _window = null;
        }
    }
}
