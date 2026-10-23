using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Iris.Core;
using Iris.Core.Settings;

namespace Iris.App.Services;

/// <summary>What a hotkey does.</summary>
public enum HotkeyAction
{
    ToggleOverlay,
    TogglePanel,
}

/// <summary>
/// The two global hotkeys, read with a low-level keyboard hook (WH_KEYBOARD_LL) instead of RegisterHotKey. Two reasons:
/// the key press always goes on to the program in front as well (the game sees it; IRIS never swallows a key), and there is nothing
/// to "register", so no other program can block a combination. IRIS therefore does not check for conflicts with other programs.
///
/// - The hook lives on its own background thread ("IRIS keyboard hook", priority AboveNormal) that does nothing but run a message loop
///   (GetMessage). Windows calls a low-level hook on the thread that installed it and silently removes a hook whose callback is not answered
///   within <c>LowLevelHooksTimeout</c>; with the hook on the UI thread, any pause of the UI (a long save, a dialog, the garbage collector)
///   would delay every keystroke on the whole system and could get the hook removed. On its own thread the UI cannot get in the way.
///   The thread is started only while at least one hotkey is set and stopped (WM_QUIT, then Join with a timeout) when none is set or on Dispose.
/// - The callback does almost nothing: it always passes the event on (<c>CallNextHookEx</c>), recognises the hotkey and posts the work to the
///   UI thread's dispatcher. It never throws.
/// - The configured keys are one immutable record in a volatile field (<see cref="Apply"/> writes it on the UI thread, the hook thread reads it);
///   the "key is held" flags belong to the hook thread alone.
/// - A hotkey fires on the key-down of its key when exactly its modifiers (Ctrl, Alt, Shift, Win) are held; it fires once and not again
///   until the key was released (auto-repeat is ignored). Injected key events (from other programs) count like real ones.
///   While a hotkey box in the Settings tab is recording a new combination, hotkeys do not fire (<see cref="CaptureInProgress"/>).
/// - The only problem IRIS reports itself is the same combination for both hotkeys (<see cref="OverlayError"/> / <see cref="PanelError"/>);
///   then neither is active. If Windows refuses the hook, that is written to the log; the next <see cref="Apply"/> tries again.
/// Create it and call its members on the UI thread. Dispose stops the hook thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12; // Alt
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int ThreadStopTimeoutMs = 2000;
    private const int ThreadStartTimeoutMs = 2000;

    /// <summary>
    /// Set by a hotkey box while it records a new combination (it has the keyboard focus); hotkeys do not fire then, so pressing the very
    /// combination that is being recorded does not toggle the overlay. Written on the UI thread, read on the hook thread.
    /// </summary>
    public static volatile bool CaptureInProgress;

    private enum HookState
    {
        Starting,
        Running,
        Failed,
    }

    /// <summary>The keys the hook thread looks for. Never changed after it was made: <see cref="Apply"/> publishes a new one.</summary>
    private sealed record HookConfig(Hotkey? Overlay, Hotkey? Panel)
    {
        public static readonly HookConfig None = new(null, null);
    }

    /// <summary>One run of the hook thread.</summary>
    private sealed class HookSession
    {
        public Thread? Thread;
        public readonly ManualResetEventSlim Ready = new(false); // set when the thread has its message queue (or has given up); not disposed on purpose, a late Set must stay harmless
        public volatile uint ThreadId;
        public volatile bool Stopping;
        public volatile HookState State = HookState.Starting;
    }

    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _callback; // a field on purpose: the delegate must live as long as the hook

    // UI thread only
    private HookSession? _session;
    private string _overlayText = string.Empty;
    private string _panelText = string.Empty;
    private bool _applied;
    private volatile bool _disposed;

    // written by the UI thread, read by the hook thread
    private volatile HookConfig _config = HookConfig.None;

    // hook thread only
    private HookConfig? _seenConfig;
    private bool _overlayHeld; // true from the firing key-down until the key comes up
    private bool _panelHeld;

    public HotkeyManager(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = HookCallback;
    }

    /// <summary>Raised (on the UI thread) when a hotkey was pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Raised after <see cref="Apply"/> changed the errors.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Set when both hotkeys are the same combination, else null.</summary>
    public string? OverlayError { get; private set; }

    /// <summary>Set when both hotkeys are the same combination, else null.</summary>
    public string? PanelError { get; private set; }

    /// <summary>The hotkey texts that were last applied (normalised, empty for none).</summary>
    public string AppliedOverlayText => _overlayText;

    public string AppliedPanelText => _panelText;

    /// <summary>
    /// Takes over the hotkeys of <paramref name="settings"/>. Can be called again at any time. Unchanged hotkeys do nothing, except that a hook
    /// that Windows refused (or that stopped by itself) is installed again.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        if (_disposed)
            return;

        var overlayText = HotkeyText.Normalize(settings.HotkeyToggleOverlay);
        var panelText = HotkeyText.Normalize(settings.HotkeyTogglePanel);
        if (_applied && overlayText == _overlayText && panelText == _panelText)
        {
            if (_session is { State: HookState.Failed })
                EnsureHooked(); // the hook could not be installed last time: try again
            return;
        }

        var oldOverlayError = OverlayError;
        var oldPanelError = PanelError;
        _applied = true;
        _overlayText = overlayText;
        _panelText = panelText;
        OverlayError = null;
        PanelError = null;

        Hotkey? overlayKey = null;
        Hotkey? panelKey = null;
        if (overlayText.Length > 0 && overlayText == panelText)
        {
            OverlayError = PanelError = $"Both hotkeys are {overlayText}. Choose two different ones; neither is active now.";
            _log.Write(_clock.UtcNow, "Hotkeys: both are " + overlayText + "; neither is active");
        }
        else
        {
            if (HotkeyText.TryParse(overlayText, out var parsedOverlay, out _))
                overlayKey = parsedOverlay;
            if (HotkeyText.TryParse(panelText, out var parsedPanel, out _))
                panelKey = parsedPanel;
            _log.Write(_clock.UtcNow,
                $"Hotkeys: Toggle Overlay = {(overlayText.Length == 0 ? "none" : overlayText)}, Toggle Info Panel = {(panelText.Length == 0 ? "none" : panelText)}");
        }

        _config = new HookConfig(overlayKey, panelKey); // the hook thread picks it up with its next key event

        if (overlayKey is null && panelKey is null)
            StopHookThread();
        else
            EnsureHooked();

        if (OverlayError != oldOverlayError || PanelError != oldPanelError)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // The hook thread
    // ---------------------------------------------------------------------------------------

    /// <summary>Starts the hook thread unless it runs; replaces one that failed.</summary>
    private void EnsureHooked()
    {
        if (_session is { } existing)
        {
            if (existing.State != HookState.Failed)
                return;
            StopHookThread(); // the failed thread has ended (or is just ending); clean it up and start afresh
        }

        var session = new HookSession();
        var thread = new Thread(() => HookThreadMain(session))
        {
            Name = "IRIS keyboard hook",
            IsBackground = true, // never keeps IRIS alive
            Priority = ThreadPriority.AboveNormal,
        };
        session.Thread = thread;
        _session = session;
        thread.Start();

        // Wait (a few milliseconds in practice) until the thread has its message queue, so that stopping it can never miss it.
        // The hook itself is installed by the thread afterwards; whether that worked shows in the log and in session.State.
        session.Ready.Wait(ThreadStartTimeoutMs);
    }

    /// <summary>Asks the hook thread to end (WM_QUIT) and waits for it a little.</summary>
    private void StopHookThread()
    {
        var session = _session;
        if (session is null)
            return;
        _session = null;
        session.Stopping = true;

        var threadId = session.ThreadId;
        if (threadId != 0 && session.State != HookState.Failed)
            NativeMethods.PostThreadMessage(threadId, NativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero); // fails harmlessly if the thread is gone

        if (session.Thread is { } thread && !thread.Join(ThreadStopTimeoutMs))
            _log.Write(_clock.UtcNow, "Hotkeys: the keyboard hook thread did not stop within 2 seconds (it is a background thread and ends with IRIS)");
    }

    /// <summary>The hook thread: makes a message queue, installs the hook, runs the message loop until WM_QUIT, removes the hook.</summary>
    private void HookThreadMain(HookSession session)
    {
        var hook = IntPtr.Zero;
        try
        {
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PmNoRemove); // creates this thread's message queue
            session.ThreadId = NativeMethods.GetCurrentThreadId();
            session.Ready.Set();

            hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                session.State = HookState.Failed;
                PostLog($"Hotkeys: the keyboard hook could not be installed (Windows error {code}: {new Win32Exception(code).Message}); hotkeys do not work");
                return;
            }

            session.State = HookState.Running;
            while (!session.Stopping)
            {
                var result = NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0);
                if (result > 0)
                    continue; // nothing is posted to this thread except WM_QUIT; the hook callbacks run inside GetMessage
                if (result < 0 && !session.Stopping)
                {
                    var code = Marshal.GetLastWin32Error();
                    session.State = HookState.Failed;
                    PostLog($"Hotkeys: the keyboard hook thread stopped (message loop error {code}); hotkeys do not work");
                }
                break; // WM_QUIT (0) or an error (-1)
            }
        }
        catch (Exception ex)
        {
            session.State = HookState.Failed;
            PostLog("Hotkeys: the keyboard hook thread failed: " + ex.Message);
        }
        finally
        {
            if (hook != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(hook);
            session.Ready.Set(); // in case something failed before the first Set: nobody should wait for the timeout
        }
    }

    /// <summary>Writes a log line from the UI thread (the log is not meant to be written from the hook thread).</summary>
    private void PostLog(string text)
    {
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                    _log.Write(_clock.UtcNow, text);
            }));
        }
        catch (Exception)
        {
            // nothing sensible left to do (the application is shutting down)
        }
    }

    // ---------------------------------------------------------------------------------------
    // The hook callback (on the hook thread)
    // ---------------------------------------------------------------------------------------

    /// <summary>Called by Windows for every key event on the system. Must be quick, must never throw, and must always pass the event on.</summary>
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                var key = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is its first field
                if (message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown)
                    OnKeyDown(key);
                else if (message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp)
                    OnKeyUp(key);
            }
            catch (Exception)
            {
                // Whatever went wrong, the key must still reach the program in front, and an exception must not leave the callback.
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // the first argument is ignored by Windows
    }

    /// <summary>The held flags belong to one configuration: after <see cref="Apply"/> published a new one they start afresh.</summary>
    private HookConfig CurrentConfig()
    {
        var config = _config;
        if (!ReferenceEquals(config, _seenConfig))
        {
            _seenConfig = config;
            _overlayHeld = false;
            _panelHeld = false;
        }
        return config;
    }

    private void OnKeyDown(int key)
    {
        var config = CurrentConfig();

        // Both hotkeys are looked at on every key-down: they may share the key and differ in the modifiers (Ctrl+O and Ctrl+Shift+O).
        if (config.Overlay is { } overlay && key == overlay.VirtualKey)
        {
            // a "held" flag whose key is in fact up lost its key-up (hook briefly removed, focus change...): forget it
            if (_overlayHeld && !IsDown(overlay.VirtualKey))
                _overlayHeld = false;
            if (!CaptureInProgress && !_overlayHeld && HeldModifiers() == overlay.Modifiers)
            {
                _overlayHeld = true;
                Post(HotkeyAction.ToggleOverlay);
            }
        }

        if (config.Panel is { } panel && key == panel.VirtualKey)
        {
            if (_panelHeld && !IsDown(panel.VirtualKey))
                _panelHeld = false;
            if (!CaptureInProgress && !_panelHeld && HeldModifiers() == panel.Modifiers)
            {
                _panelHeld = true;
                Post(HotkeyAction.TogglePanel);
            }
        }
    }

    private void OnKeyUp(int key)
    {
        var config = CurrentConfig();
        if (config.Overlay is { } overlay && key == overlay.VirtualKey)
            _overlayHeld = false;
        if (config.Panel is { } panel && key == panel.VirtualKey)
            _panelHeld = false;
    }

    private static HotkeyModifiers HeldModifiers()
    {
        var held = HotkeyModifiers.None;
        if (IsDown(VkControl))
            held |= HotkeyModifiers.Control;
        if (IsDown(VkMenu))
            held |= HotkeyModifiers.Alt;
        if (IsDown(VkShift))
            held |= HotkeyModifiers.Shift;
        if (IsDown(VkLWin) || IsDown(VkRWin))
            held |= HotkeyModifiers.Windows;
        return held;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void Post(HotkeyAction action) =>
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
                Pressed?.Invoke(this, action);
        }));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopHookThread();
    }
}
