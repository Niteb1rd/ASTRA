using System;
using System.ComponentModel;
using System.Windows.Interop;
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
/// The two global hotkeys. They are registered with RegisterHotKey on a hidden message-only window, so they work whatever program is in front
/// (the game included). <see cref="Apply"/> registers what the settings say and can be called again at any time (it first drops the old ones).
/// A hotkey that cannot be registered (another program has it) or that is the same for both actions is not registered and gets an error text
/// (<see cref="OverlayError"/> / <see cref="PanelError"/>) which the Settings tab shows in red; every problem is also written to the log.
/// Create and use it on the UI thread. Dispose unregisters everything.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int OverlayId = 0x4952;   // arbitrary ids, only meaningful for this window
    private const int PanelId = 0x4953;

    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly HwndSource _window;

    private string _overlayText = string.Empty;
    private string _panelText = string.Empty;
    private bool _overlayRegistered;
    private bool _panelRegistered;
    private bool _applied;
    private bool _disposed;

    public HotkeyManager(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
        // parent = HWND_MESSAGE: a window that only receives messages and is never shown
        _window = new HwndSource(0, 0, 0, 0, 0, "IRIS hotkeys", NativeMethods.HwndMessage);
        _window.AddHook(WndProc);
    }

    /// <summary>Raised (on the UI thread) when a registered hotkey is pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Raised after <see cref="Apply"/> changed the errors.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Why the "Toggle Overlay" hotkey is not working, or null (also null when none is set).</summary>
    public string? OverlayError { get; private set; }

    /// <summary>Why the "Toggle Info Panel" hotkey is not working, or null.</summary>
    public string? PanelError { get; private set; }

    /// <summary>The hotkey texts that were last applied (normalised, empty for none).</summary>
    public string AppliedOverlayText => _overlayText;

    public string AppliedPanelText => _panelText;

    /// <summary>Registers the hotkeys of <paramref name="settings"/>. Does nothing when they are what is already applied and registered fine.</summary>
    public void Apply(AppSettings settings)
    {
        if (_disposed)
            return;

        var overlayText = HotkeyText.Normalize(settings.HotkeyToggleOverlay);
        var panelText = HotkeyText.Normalize(settings.HotkeyTogglePanel);
        if (_applied && overlayText == _overlayText && panelText == _panelText && OverlayError is null && PanelError is null)
            return;

        UnregisterAll();
        _overlayText = overlayText;
        _panelText = panelText;
        _applied = true;
        var oldOverlayError = OverlayError;
        var oldPanelError = PanelError;
        OverlayError = null;
        PanelError = null;

        if (overlayText.Length > 0 && overlayText == panelText)
        {
            OverlayError = PanelError = $"Both hotkeys are {overlayText}. Choose two different ones; neither is active now.";
            _log.Write(_clock.UtcNow, "Hotkeys: both are " + overlayText + "; neither was registered");
        }
        else
        {
            _overlayRegistered = TryRegister(OverlayId, overlayText, "Toggle Overlay", out var overlayError);
            OverlayError = overlayError;
            _panelRegistered = TryRegister(PanelId, panelText, "Toggle Info Panel", out var panelError);
            PanelError = panelError;
        }

        if (OverlayError != oldOverlayError || PanelError != oldPanelError)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryRegister(int id, string text, string name, out string? error)
    {
        error = null;
        if (text.Length == 0)
            return false;
        if (!HotkeyText.TryParse(text, out var hotkey, out _))
            return false; // cannot happen: the text was normalised

        if (NativeMethods.RegisterHotKey(_window.Handle, id, (uint)hotkey!.Modifiers | NativeMethods.ModNoRepeat, (uint)hotkey.VirtualKey))
        {
            _log.Write(_clock.UtcNow, $"Hotkey {name}: {text} registered");
            return true;
        }

        var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        error = $"{text} could not be registered; another program probably uses it. Choose a different one.";
        _log.Write(_clock.UtcNow, $"Hotkey {name}: {text} could not be registered (Windows error {code}: {new Win32Exception(code).Message})");
        return false;
    }

    private void UnregisterAll()
    {
        if (_overlayRegistered)
            NativeMethods.UnregisterHotKey(_window.Handle, OverlayId);
        if (_panelRegistered)
            NativeMethods.UnregisterHotKey(_window.Handle, PanelId);
        _overlayRegistered = false;
        _panelRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == OverlayId && _overlayRegistered)
            {
                handled = true;
                Pressed?.Invoke(this, HotkeyAction.ToggleOverlay);
            }
            else if (id == PanelId && _panelRegistered)
            {
                handled = true;
                Pressed?.Invoke(this, HotkeyAction.TogglePanel);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
