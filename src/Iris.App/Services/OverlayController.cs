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
///   borderless (<see cref="GameWindowTracker"/>), the overlay is not paused, and there is something to draw;
/// - the test overlay (<see cref="StartTest"/>), shown for 15 seconds whatever is in front.
/// The window is created the first time it is needed and afterwards only hidden. While the game is not in front the only work is the
/// tracker's 250 ms foreground check.
/// </summary>
public sealed class OverlayController : IDisposable
{
    public const int TestSeconds = 15;

    private readonly AppServices _services;
    private readonly GameWindowTracker _tracker;
    private readonly BorderlessNoticeTracker _notice = new();
    private readonly DispatcherTimer _testTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private OverlayWindow? _window;
    private OverlayModel _liveModel = OverlayModel.Empty;
    private bool _paused;
    private bool _disposed;

    // the running test overlay (null model = none)
    private OverlayModel? _testModel;
    private PixelRect _testBounds;
    private AppSettings? _testSettings;
    private long _testEndTick;

    public OverlayController(AppServices services)
    {
        _services = services;
        _tracker = new GameWindowTracker(services.Log, services.Clock);
        _tracker.Changed += OnGameWindowChanged;
        _testTimer.Tick += OnTestTick;
        services.SettingsApplied += OnSettingsApplied;
    }

    /// <summary>Raised (on the UI thread) when the test overlay starts, ends, or its countdown moves on.</summary>
    public event EventHandler? TestStateChanged;

    /// <summary>The tray's "Pause overlay": while true the live overlay is hidden. The test overlay is not affected (it is an explicit request).</summary>
    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value)
                return;
            _paused = value;
            _services.IsOverlayPaused = value;
            _services.Log.Write(_services.Clock.UtcNow, value ? "Overlay paused" : "Overlay resumed");
            Evaluate();
        }
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
    public void Start() => _tracker.Start();

    /// <summary>Gives the overlay the detections to show while the game is in front (Phase 3). The empty model hides the overlay.</summary>
    public void SetLiveModel(OverlayModel model)
    {
        _liveModel = model;
        Evaluate();
    }

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
        if (snapshot.State == GameWindowState.NotBorderless && _notice.ShouldNotify(snapshot.ProcessId))
        {
            _services.Log.Write(_services.Clock.UtcNow, "Told the user that Star Citizen is not in borderless windowed mode");
            _services.Notify?.Invoke(BorderlessNoticeTracker.Text);
        }
        Evaluate();
    }

    private void OnSettingsApplied(object? sender, EventArgs e)
    {
        // settings are normally applied on the UI thread; if not, the window must still be touched on it
        var dispatcher = _testTimer.Dispatcher;
        if (dispatcher.CheckAccess())
            Evaluate();
        else
            dispatcher.BeginInvoke(new Action(Evaluate));
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
        if (_paused || snapshot.State != GameWindowState.Ready || !HasSomethingToDraw(_liveModel, settings))
        {
            _window?.HideOverlay();
            return;
        }

        EnsureWindow().Present(_liveModel, snapshot.Bounds, settings);
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
