using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Iris.App.Views;

/// <summary>Small helper for dialogs that belong to the config window, and the pop-up policy for dialogs that appear on their own.</summary>
internal static class DialogHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// True when the window the user is working in (the foreground window) belongs to IRIS itself. False when it is another program
    /// (the game, say), when there is no foreground window, or when the question cannot be answered.
    /// </summary>
    public static bool IsIrisForeground()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            if (GetWindowThreadProcessId(foreground, out var processId) == 0)
                return false;
            return processId == (uint)Environment.ProcessId;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // not Windows: nothing to compare with, so never claim to be in front
        }
    }

    /// <summary>
    /// Gets <paramref name="owner"/> ready to own a dialog: a minimized window is restored and brought to the front, so the dialog
    /// cannot sit hidden behind it. Returns false when the window cannot own a dialog (not shown, or already closed).
    /// </summary>
    public static bool PrepareOwner(Window? owner)
    {
        if (owner is null || !owner.IsLoaded || !owner.IsVisible)
            return false;

        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();
        return true;
    }

    /// <summary>
    /// The pop-up policy for a dialog that appears WITHOUT the user having just acted in IRIS (startup problems, the 24-hour update question).
    /// Call it right before <c>ShowDialog</c>.
    /// - IRIS is the foreground program: an ordinary dialog, centered on <paramref name="owner"/> if that window is open, else on the screen.
    /// - Another program is in front (the game): the dialog is never forced over it. It is not topmost and not activated, it has a taskbar
    ///   button, and a tray balloon with <paramref name="balloonText"/> tells the user that it is waiting.
    /// - <paramref name="userJustActed"/> is true when the dialog is the direct follow-up of something the user just did in IRIS (they
    /// answered Yes to an earlier question, or launched IRIS): it is shown normally (activated, with a taskbar button when it has no
    /// owner, no balloon) even if IRIS is not the foreground program at that moment.
    /// </summary>
    public static void PrepareBackgroundDialog(
        Window dialog, Window? owner, string balloonText, Action<string>? notify, bool userJustActed = false)
    {
        dialog.Topmost = false;

        if (userJustActed || IsIrisForeground())
        {
            if (owner is not null && PrepareOwner(owner))
            {
                dialog.Owner = owner;
                dialog.ShowInTaskbar = false; // belongs to the open window, like a message box
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.ShowInTaskbar = true;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return;
        }

        // No owner and no PrepareOwner here on purpose: restoring or activating the config window would pull IRIS in front of the game.
        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = true;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            notify?.Invoke(balloonText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // the tray icon is gone (IRIS is closing): the dialog is still shown
        }
    }
}
