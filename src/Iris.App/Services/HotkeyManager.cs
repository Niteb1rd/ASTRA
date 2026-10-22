using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
/// - The hook is installed on the UI thread (its message loop is the WPF dispatcher's) and only while at least one hotkey is set.
/// - The callback does almost nothing: it always passes the event on (<c>CallNextHookEx</c>), recognises the hotkey and posts the work to the
///   dispatcher. Windows silently drops a hook whose callback is slow.
/// - A hotkey fires on the key-down of its key when exactly its modifiers (Ctrl, Alt, Shift, Win) are held; it fires once and not again
///   until the key was released (auto-repeat is ignored). Injected key events (from other programs) count like real ones.
/// - The only problem IRIS reports itself is the same combination for both hotkeys (<see cref="OverlayError"/> / <see cref="PanelError"/>);
///   then neither is active.
/// Create and use it on the UI thread. Dispose removes the hook.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12; // Alt
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _callback; // a field on purpose: the delegate must live as long as the hook

    private IntPtr _hook = IntPtr.Zero;
    private Hotkey? _overlayKey;
    private Hotkey? _panelKey;
    private bool _overlayHeld; // true from the firing key-down until the key comes up
    private bool _panelHeld;
    private string _overlayText = string.Empty;
    private string _panelText = string.Empty;
    private bool _applied;
    private bool _disposed;

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

    /// <summary>Takes over the hotkeys of <paramref name="settings"/>. Can be called again at any time.</summary>
    public void Apply(AppSettings settings)
    {
        if (_disposed)
            return;

        var overlayText = HotkeyText.Normalize(settings.HotkeyToggleOverlay);
        var panelText = HotkeyText.Normalize(settings.HotkeyTogglePanel);
        if (_applied && overlayText == _overlayText && panelText == _panelText)
            return;

        var oldOverlayError = OverlayError;
        var oldPanelError = PanelError;
        _applied = true;
        _overlayText = overlayText;
        _panelText = panelText;
        _overlayHeld = false;
        _panelHeld = false;
        _overlayKey = null;
        _panelKey = null;
        OverlayError = null;
        PanelError = null;

        if (overlayText.Length > 0 && overlayText == panelText)
        {
            OverlayError = PanelError = $"Both hotkeys are {overlayText}. Choose two different ones; neither is active now.";
            _log.Write(_clock.UtcNow, "Hotkeys: both are " + overlayText + "; neither is active");
        }
        else
        {
            if (HotkeyText.TryParse(overlayText, out var overlayKey, out _))
                _overlayKey = overlayKey;
            if (HotkeyText.TryParse(panelText, out var panelKey, out _))
                _panelKey = panelKey;
            _log.Write(_clock.UtcNow,
                $"Hotkeys: Toggle Overlay = {(overlayText.Length == 0 ? "none" : overlayText)}, Toggle Info Panel = {(panelText.Length == 0 ? "none" : panelText)}");
        }

        if (_overlayKey is null && _panelKey is null)
            Unhook();
        else
            EnsureHooked();

        if (OverlayError != oldOverlayError || PanelError != oldPanelError)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureHooked()
    {
        if (_hook != IntPtr.Zero)
            return;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            _log.Write(_clock.UtcNow, $"Hotkeys: the keyboard hook could not be installed (Windows error {code}: {new Win32Exception(code).Message}); hotkeys do not work");
        }
    }

    private void Unhook()
    {
        if (_hook == IntPtr.Zero)
            return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Called by Windows for every key event on the system. Must be quick, and must always pass the event on.</summary>
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
                var key = (int)data.VirtualKey;
                if (message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown)
                    OnKeyDown(key);
                else if (message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp)
                    OnKeyUp(key);
            }
            catch (Exception ex) when (ex is ArgumentException or NullReferenceException)
            {
                // never let anything stop the key from reaching the program in front
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void OnKeyDown(int key)
    {
        if (_overlayKey is { } overlay && key == overlay.VirtualKey)
        {
            if (!_overlayHeld && HeldModifiers() == overlay.Modifiers)
            {
                _overlayHeld = true;
                Post(HotkeyAction.ToggleOverlay);
            }
        }
        else if (_panelKey is { } panel && key == panel.VirtualKey)
        {
            if (!_panelHeld && HeldModifiers() == panel.Modifiers)
            {
                _panelHeld = true;
                Post(HotkeyAction.TogglePanel);
            }
        }
    }

    private void OnKeyUp(int key)
    {
        if (_overlayKey is { } overlay && key == overlay.VirtualKey)
            _overlayHeld = false;
        if (_panelKey is { } panel && key == panel.VirtualKey)
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
        Unhook();
    }
}
