using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Services;
using Iris.Core.Settings;

namespace Iris.App.Views;

/// <summary>
/// A box that records a hotkey. Recording starts only when the user CLICKS the box with the mouse, or presses Enter or Space while the box has
/// the keyboard focus; merely getting the focus (Tab, or the window's own focus handling) does not record anything. Then the user presses a key,
/// with or without Ctrl, Alt, Shift or Win; a single key is fine. Esc cancels (and can never be assigned), and so does leaving the box.
/// While recording, the global hotkeys do not fire (<see cref="HotkeyManager.CaptureInProgress"/>). The recorded hotkey is the text
/// <see cref="HotkeyValue"/> ("Ctrl+Shift+O", "F9", empty for none); <see cref="HotkeyChanged"/> is raised when the user records or clears one.
/// The text is only shown, never typed.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private const string NoneText = "(none)";
    private const string CapturingText = "Press keys... (Esc cancels)";

    private string _value = string.Empty;
    private bool _capturing;
    private string? _problem;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ContextMenu = null;
        ShowText();
        Unloaded += (_, _) => EndCapture(); // a box that goes away while it has the focus must not leave the hotkeys switched off
    }

    /// <summary>Raised after the user recorded or cleared a hotkey (not when it is set from code).</summary>
    public event EventHandler? HotkeyChanged;

    /// <summary>The hotkey as text, or empty for none. Setting it does not raise <see cref="HotkeyChanged"/>.</summary>
    public string HotkeyValue
    {
        get => _value;
        set
        {
            _value = HotkeyText.Normalize(value);
            _problem = null;
            ShowText();
        }
    }

    /// <summary>Removes the hotkey (the Clear button).</summary>
    public void ClearHotkey()
    {
        if (_value.Length == 0)
            return;
        _value = string.Empty;
        _problem = null;
        ShowText();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Select(0, 0); // a Tab into the box selects all of its text; the focused border is enough
        ShowText(); // focus alone starts nothing
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        EndCapture();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        e.Handled = true; // no caret and no text selection: a click only starts the recording
        if (!IsKeyboardFocused)
            Focus(); // another box that was recording loses the focus (and so ends its recording) first
        BeginCapture();
    }

    private void BeginCapture()
    {
        if (_capturing)
            return;
        _capturing = true;
        HotkeyManager.CaptureInProgress = true; // the global hotkeys stay quiet while a hotkey is recorded
        _problem = null;
        ShowText();
    }

    private void EndCapture()
    {
        if (_capturing)
            HotkeyManager.CaptureInProgress = false;
        _capturing = false;
        _problem = null;
        ShowText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            if ((e.Key is Key.Enter or Key.Space) && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true; // not typed, and Enter must not press the window's default button
                if (!e.IsRepeat)
                    BeginCapture();
                return;
            }
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true; // everything goes to the recorder, also Esc (which must not close the window) and Alt combinations
        if (e.IsRepeat)
            return; // a key that is held down (for example the Enter that started the recording) is not a new press
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
            case Key.LeftAlt:
            case Key.RightAlt:
            case Key.LeftShift:
            case Key.RightShift:
            case Key.LWin:
            case Key.RWin:
            case Key.None:
            case Key.DeadCharProcessed:
            case Key.ImeProcessed:
                return; // a modifier on its own: keep waiting for the real key

            case Key.Escape:
                EndCapture(); // cancelled: the box keeps the keyboard focus but stops recording
                return;
        }

        var modifiers = HotkeyModifiers.None;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control))
            modifiers |= HotkeyModifiers.Control;
        if (held.HasFlag(ModifierKeys.Alt))
            modifiers |= HotkeyModifiers.Alt;
        if (held.HasFlag(ModifierKeys.Shift))
            modifiers |= HotkeyModifiers.Shift;
        if (held.HasFlag(ModifierKeys.Windows))
            modifiers |= HotkeyModifiers.Windows;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!HotkeyText.TryCreate(modifiers, virtualKey, out var hotkey, out var reason))
        {
            _problem = reason;
            ShowText();
            return;
        }

        _value = HotkeyText.Format(hotkey!);
        EndCapture(); // recorded: stop recording, show the new hotkey
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowText()
    {
        if (_capturing)
        {
            Text = _problem ?? CapturingText;
            Foreground = _problem is null ? SystemColors.ControlTextBrush : Brushes.Firebrick;
        }
        else if (_value.Length == 0)
        {
            Text = NoneText;
            Foreground = SystemColors.GrayTextBrush;
        }
        else
        {
            Text = _value;
            Foreground = SystemColors.ControlTextBrush;
        }
    }
}
