using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Overlay;

namespace Iris.App.Services;

/// <summary>
/// What the tracker saw at the last check: the state, and for a game in front its window rectangle (screen pixels), process, window style flags
/// and the rectangle of its monitor (the last two are what the borderless decision was made from; they are logged for diagnosing it).
/// </summary>
public sealed record GameWindowSnapshot(GameWindowState State, PixelRect Bounds, int ProcessId, IntPtr Handle, uint Style = 0, PixelRect Monitor = default)
{
    /// <summary>Star Citizen is not in front (or not running).</summary>
    public static readonly GameWindowSnapshot None = new(GameWindowState.GameNotActive, default, 0, IntPtr.Zero);
}

/// <summary>
/// Watches whether Star Citizen is the window in front and whether it is borderless (<see cref="GameWindowRules"/>).
/// Every 250 ms it asks Windows for the foreground window and which process owns it; the process name is remembered per process id,
/// so nothing else happens while another program is in front (a "not the game" answer is kept for 5 seconds, a "game" answer for 60). Only when the game is in front are its rectangle, its monitor and its
/// style read. <see cref="Changed"/> is raised (on the UI thread) when the snapshot differs from the previous one; state changes are logged. <see cref="Checked"/> is raised after every check.
/// </summary>
public sealed class GameWindowTracker : IDisposable
{
    /// <summary>The game's process name (StarCitizen.exe), compared without regard to upper/lower case.</summary>
    public const string GameProcessName = "StarCitizen";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);
    // A process id can be reused by another program after the game ended, so a "not the game" answer is only trusted for a few seconds
    // (a game that starts must be noticed quickly); a "this is the game" answer may stay longer.
    private const long GameProcessCacheMs = 60_000;
    private const long OtherProcessCacheMs = 5_000;
    private const int ProcessNameCacheMaxEntries = 256;

    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<uint, (bool IsGame, long CheckedAtMs)> _processCache = new();

    public GameWindowTracker(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>The result of the last check.</summary>
    public GameWindowSnapshot Current { get; private set; } = GameWindowSnapshot.None;

    /// <summary>Raised when the state, the game's rectangle or the game's process changed.</summary>
    public event EventHandler<GameWindowSnapshot>? Changed;

    /// <summary>
    /// Raised (on the UI thread) after EVERY check, whether or not anything changed. For decisions that must not depend on a change happening
    /// (the "not borderless" notice: the game may already have been in that state when IRIS started).
    /// </summary>
    public event EventHandler<GameWindowSnapshot>? Checked;

    public void Start()
    {
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        GameWindowSnapshot snapshot;
        try
        {
            snapshot = Evaluate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            snapshot = GameWindowSnapshot.None; // Windows would not answer: treat it as "no game in front"
        }

        var previous = Current;
        if (snapshot != previous)
        {
            Current = snapshot;
            if (previous.State != snapshot.State)
                LogChange(previous, snapshot);
            Changed?.Invoke(this, snapshot);
        }

        Checked?.Invoke(this, Current); // every check, changed or not
    }

    private GameWindowSnapshot Evaluate()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return GameWindowSnapshot.None;
        if (NativeMethods.GetWindowThreadProcessId(foreground, out var processId) == 0 || processId == 0)
            return GameWindowSnapshot.None;
        if (!IsGameProcess(processId))
            return GameWindowSnapshot.None;

        // The game is in front: now (and only now) read its window.
        if (NativeMethods.IsIconic(foreground)
            || !NativeMethods.TryGetWindowBounds(foreground, out var window)
            || !NativeMethods.TryGetMonitorBounds(foreground, out var monitor))
            return GameWindowSnapshot.None;

        var style = NativeMethods.GetStyle(foreground);
        var state = GameWindowRules.Decide(true, window, monitor, style);
        return new GameWindowSnapshot(state, window, (int)processId, foreground, style, monitor);
    }

    private bool IsGameProcess(uint processId)
    {
        var now = Environment.TickCount64;
        if (_processCache.TryGetValue(processId, out var known) && now - known.CheckedAtMs < (known.IsGame ? GameProcessCacheMs : OtherProcessCacheMs))
            return known.IsGame;

        var isGame = false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            isGame = string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // the process ended meanwhile, or Windows would not tell its name: not the game as far as we can tell
        }

        if (_processCache.Count >= ProcessNameCacheMaxEntries)
            _processCache.Clear();
        _processCache[processId] = (isGame, now);
        return isGame;
    }

    /// <summary>
    /// Looks for a running Star Citizen window anywhere (in front or not) and gives its rectangle in screen pixels.
    /// For the test overlay only; not used by the 250 ms check.
    /// </summary>
    public bool TryFindGameWindow(out PixelRect bounds)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(GameProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            bounds = default;
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                IntPtr handle;
                try
                {
                    handle = process.MainWindowHandle;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    continue; // the process just ended
                }
                if (handle != IntPtr.Zero && !NativeMethods.IsIconic(handle) && NativeMethods.TryGetWindowBounds(handle, out bounds))
                    return true;
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        bounds = default;
        return false;
    }

    private void LogChange(GameWindowSnapshot from, GameWindowSnapshot to)
    {
        var detail = to.State == GameWindowState.GameNotActive
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, " (process {0}, window {1}x{2} at {3},{4}, style 0x{5:X8}, monitor {6}x{7} at {8},{9})",
                to.ProcessId, to.Bounds.Width, to.Bounds.Height, to.Bounds.X, to.Bounds.Y,
                to.Style, to.Monitor.Width, to.Monitor.Height, to.Monitor.X, to.Monitor.Y);
        _log.Write(_clock.UtcNow, $"Game window: {from.State} -> {to.State}{detail}");
    }
}
