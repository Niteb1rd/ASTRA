using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.Core.Settings;

namespace Iris.App.Views;

/// <summary>
/// A box that records a hotkey: click it, then press the combination (at least one of Ctrl, Alt, Shift or Win plus a key).
/// Esc cancels. The recorded combination is the text <see cref="HotkeyValue"/> ("Ctrl+Shift+O", empty for none); <see cref="HotkeyChanged"/>
/// is raised when the user records or clears one. The text is only shown, never typed.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private const string NoneText = "(none) - click, then press keys";
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
    public void Clear()
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
        _capturing = true;
        _problem = null;
        ShowText();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _capturing = false;
        _problem = null;
        ShowText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true; // everything goes to the recorder, also Esc (which must not close the window) and Alt combinations
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
                LeaveBox();
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

        if (modifiers == HotkeyModifiers.None)
        {
            _problem = "Hold Ctrl, Alt, Shift or Win too";
            ShowText();
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!HotkeyText.TryCreate(modifiers, virtualKey, out var hotkey))
        {
            _problem = "That key cannot be used";
            ShowText();
            return;
        }

        _value = HotkeyText.Format(hotkey!);
        _problem = null;
        ShowText();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
        LeaveBox();
    }

    /// <summary>Ends the recording by moving the keyboard focus on.</summary>
    private void LeaveBox() => MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));

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
