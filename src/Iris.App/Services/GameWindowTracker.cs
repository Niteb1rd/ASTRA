using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Overlay;

namespace Iris.App.Services;

/// <summary>
/// What the tracker saw at the last check: the state, and for a game in front its window rectangle (screen pixels), process, window style flags
/// and the rectangle of its monitor, plus the game's own Display Mode setting when it could be read (null = unknown). These are what the
/// borderless decision was made from; they are logged for diagnosing it.
/// </summary>
public sealed record GameWindowSnapshot(GameWindowState State, PixelRect Bounds, int ProcessId, IntPtr Handle, uint Style = 0, PixelRect Monitor = default, WindowMode? Mode = null)
{
    /// <summary>Star Citizen is not in front (or not running).</summary>
    public static readonly GameWindowSnapshot None = new(GameWindowState.GameNotActive, default, 0, IntPtr.Zero);
}

/// <summary>
/// Watches whether Star Citizen is the window in front and whether it is borderless (<see cref="GameWindowRules"/>).
/// Every 250 ms it asks Windows for the foreground window and which process owns it; the process name is remembered per process id,
/// so nothing else happens while another program is in front (a "not the game" answer is kept for 5 seconds, a "game" answer for 60). Only when the game is in front are its rectangle, its monitor and its
/// style read, and the game's Display Mode (<see cref="StarCitizenDisplaySettings"/>: the WindowMode line of the attributes.xml that belongs to the
/// running exe; read only, read once per IRIS run). <see cref="Changed"/> is raised (on the UI thread) when the snapshot differs from the previous one; state changes are logged. <see cref="Checked"/> is raised after every check.
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
    private readonly Dictionary<uint, GameProcessInfo> _gameInfo = new();

    private const int GameInfoMaxEntries = 16;

    // Display modes read so far, by attributes.xml path: each file is read ONCE per IRIS run (the user rarely changes the mode; restarting
    // IRIS reads it again). A different install (LIVE, PTU, ...) has its own file and is read once too.
    private readonly Dictionary<string, WindowMode?> _modeByFile = new(StringComparer.OrdinalIgnoreCase);

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
        var mode = ReadGameMode(processId);
        var state = GameWindowRules.Decide(true, window, monitor, style, mode);
        return new GameWindowSnapshot(state, window, (int)processId, foreground, style, monitor, mode);
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
        if (isGame)
            RefreshGameInfo(processId); // the exe of this process (a process id can be reused by another install's game)
        return isGame;
    }

    /// <summary>What is remembered about one running game process: its exe, its display settings file and the last mode read from it.</summary>
    private sealed class GameProcessInfo
    {
        public GameProcessInfo(string? exePath) => ExePath = exePath;

        public string? ExePath { get; }
        public string? AttributesPath { get; set; }
        public bool NotFoundLogged { get; set; }
        public bool UnusableLogged { get; set; }
        public bool NeedsRead { get; set; } = true;
        public WindowMode? Mode { get; set; }
        public WindowMode? LoggedMode { get; set; }
    }

    /// <summary>Asks Windows for the exe of the game process and keeps the entry for it (a new entry when the exe is a different one).</summary>
    private GameProcessInfo RefreshGameInfo(uint processId)
    {
        var exe = NativeMethods.TryGetProcessImagePath(processId, out var path) ? path : null;
        if (_gameInfo.TryGetValue(processId, out var existing)
            && (exe is null || string.Equals(exe, existing.ExePath, StringComparison.OrdinalIgnoreCase)))
            return existing;

        if (_gameInfo.Count >= GameInfoMaxEntries)
            _gameInfo.Clear();
        var created = new GameProcessInfo(exe);
        _gameInfo[processId] = created;
        return created;
    }

    /// <summary>
    /// The game's Display Mode from its own attributes.xml (null = unknown: the window style check decides then). The file is found relative to the
    /// running exe the first time the game is in front, and read ONCE per IRIS run; after that the value read then is used. A read that fails
    /// (file locked or half written) is tried again at the next check until it succeeds.
    /// </summary>
    private WindowMode? ReadGameMode(uint processId)
    {
        if (!_gameInfo.TryGetValue(processId, out var info))
            info = RefreshGameInfo(processId);

        if (info.AttributesPath is not null && _modeByFile.TryGetValue(info.AttributesPath, out var known))
            return known; // already read during this run
        if (!info.NeedsRead)
            return info.Mode;

        if (info.AttributesPath is null)
        {
            if (info.ExePath is not null)
                info.AttributesPath = StarCitizenDisplaySettings.AttributesPathFor(info.ExePath, File.Exists, FindAttributesCandidates);
            if (info.AttributesPath is null)
            {
                info.NeedsRead = false; // not looked for again for this game process
                if (!info.NotFoundLogged)
                {
                    info.NotFoundLogged = true;
                    _log.Write(_clock.UtcNow, "Star Citizen display settings (attributes.xml) not found, using the window check");
                }
                return info.Mode;
            }
            _log.Write(_clock.UtcNow, $"Star Citizen display settings file: {info.AttributesPath}");
        }

        var text = TryReadText(info.AttributesPath);
        if (text is null)
            return info.Mode; // locked or gone for the moment: keep the last known value, try again at the next check

        var parsed = StarCitizenDisplaySettings.ParseWindowMode(text);
        if (parsed is null && info.Mode is not null)
        {
            // possibly a half-written file: keep the last known value and try again at the next check
            if (!info.UnusableLogged)
            {
                info.UnusableLogged = true;
                _log.Write(_clock.UtcNow, $"Star Citizen display settings: WindowMode could not be read, keeping {info.Mode}");
            }
            return info.Mode;
        }

        info.NeedsRead = false;
        info.Mode = parsed;
        _modeByFile[info.AttributesPath] = parsed;
        if (parsed is not null)
        {
            info.UnusableLogged = false;
            if (parsed != info.LoggedMode)
            {
                info.LoggedMode = parsed;
                _log.Write(_clock.UtcNow, $"Star Citizen display mode: {parsed}");
            }
        }
        else if (!info.UnusableLogged)
        {
            info.UnusableLogged = true;
            _log.Write(_clock.UtcNow, "Star Citizen display settings: no usable WindowMode value, using the window check");
        }
        return info.Mode;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every <c>&lt;client&gt;\*\Profiles\default\attributes.xml</c> that exists, with its last write time (UTC).</summary>
    private static IEnumerable<(string path, DateTime modifiedUtc)> FindAttributesCandidates(string clientDirectory)
    {
        var found = new List<(string path, DateTime modifiedUtc)>();
        try
        {
            if (!Directory.Exists(clientDirectory))
                return found;
            foreach (var profile in Directory.EnumerateDirectories(clientDirectory))
            {
                var file = Path.Combine(profile, "Profiles", "default", "attributes.xml");
                if (File.Exists(file))
                    found.Add((file, File.GetLastWriteTimeUtc(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what was found so far is used
        }
        return found;
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
        // Once the game's own display mode is known (logged as "Star Citizen display mode: ..."), the window details add nothing:
        // only log them when IRIS has to rely on the window check, where they help diagnose a wrong decision.
        if (from.Mode is not null || to.Mode is not null)
            return;
        var detail = to.State == GameWindowState.GameNotActive
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, " (process {0}, window {1}x{2} at {3},{4}, style 0x{5:X8}, monitor {6}x{7} at {8},{9}, game display mode {10})",
                to.ProcessId, to.Bounds.Width, to.Bounds.Height, to.Bounds.X, to.Bounds.Y,
                to.Style, to.Monitor.Width, to.Monitor.Height, to.Monitor.X, to.Monitor.Y,
                to.Mode?.ToString() ?? "unknown");
        _log.Write(_clock.UtcNow, $"Game window: {from.State} -> {to.State}{detail}");
    }
}
