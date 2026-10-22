using System.Windows;

namespace Astra.App.Views;

/// <summary>Small helper for dialogs that belong to the config window.</summary>
internal static class DialogHelper
{
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
}
