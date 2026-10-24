using System;
using System.Windows;

namespace Iris.App.Views;

/// <summary>
/// IRIS's own small message box ("IRIS" as the title, a message, Yes/No or OK). It exists so that a question that appears on its own can be
/// shown without taking the focus away from the game: <see cref="DialogHelper.PrepareBackgroundDialog"/> decides how it is shown.
/// Use <see cref="Ask"/> for a Yes/No question, <see cref="Tell"/> for a message with OK and <see cref="ShowNotice"/> for a message with Close.
/// Closing the window counts as No. <see cref="IsAnyOpen"/> says whether one of the (modal) questions and messages is on screen; the notice
/// is not modal and does not count (it blocks nothing; <see cref="CloseNotice"/> closes it).
/// </summary>
public partial class PromptDialog : Window
{
    private static int _openCount; // only touched on the UI thread

    private static PromptDialog? _notice; // the open notice, if any; only touched on the UI thread

    private bool _yes;
    private bool _modeless; // shown with Show() (the notice): the buttons close the window instead of setting DialogResult

    private PromptDialog(string message, bool yesNo, bool defaultYes, string okText = "OK")
    {
        InitializeComponent();
        MessageText.Text = message;
        if (yesNo)
        {
            YesButton.IsDefault = defaultYes;
            NoButton.IsDefault = !defaultYes;
        }
        else
        {
            YesButton.Content = okText;
            YesButton.IsCancel = true; // Escape closes it
            NoButton.Visibility = Visibility.Collapsed;
            NoButton.IsCancel = false;
        }
    }

    /// <summary>True while any <see cref="PromptDialog"/> is on screen (its Yes/No question, OK message or Close message).</summary>
    public static bool IsAnyOpen => _openCount > 0;

    private static void ShowCounted(PromptDialog dialog)
    {
        _openCount++;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            _openCount--;
        }
    }

    /// <summary>
    /// Asks a Yes/No question. True only for Yes.
    /// </summary>
    /// <param name="message">The question.</param>
    /// <param name="balloonText">Tray balloon shown when the question has to wait in the background (another program is in front).</param>
    /// <param name="notify">Shows a tray balloon; may be null.</param>
    /// <param name="owner">The config window if it is open (used only when IRIS is the foreground program), else null.</param>
    /// <param name="defaultYes">True when Yes is the default button.</param>
    /// <param name="userJustActed">True when the question follows something the user just did in IRIS: it is then shown normally, with no balloon.</param>
    public static bool Ask(
        string message, string balloonText, Action<string>? notify, Window? owner, bool defaultYes = true, bool userJustActed = false)
    {
        var dialog = new PromptDialog(message, yesNo: true, defaultYes);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify, userJustActed);
        ShowCounted(dialog);
        return dialog._yes;
    }

    /// <summary>Shows a message with a single button: OK, or <paramref name="okText"/> ("Close", say). It is modal; closing the window counts as pressing it.</summary>
    public static void Tell(string message, string balloonText, Action<string>? notify, Window? owner, bool userJustActed = false, string okText = "OK")
    {
        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true, okText);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify, userJustActed);
        ShowCounted(dialog);
    }

    /// <summary>
    /// Shows a message with a single Close button as an ordinary (non-modal) window, so it blocks nothing: the tray menu, Settings and Quit keep
    /// working while it is open. No tray balloon. It follows the pop-up policy: with another program in front (the game) it is not activated and
    /// waits in the taskbar; if IRIS is in front it is shown normally. Only one notice exists at a time (a second call does nothing).
    /// Returns at once; <see cref="CloseNotice"/> closes it from code.
    /// </summary>
    public static void ShowNotice(string message)
    {
        if (_notice is not null)
            return;

        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true, okText: "Close") { _modeless = true };
        DialogHelper.PrepareBackgroundDialog(dialog, owner: null, balloonText: string.Empty, notify: null);
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_notice, dialog))
                _notice = null;
        };
        _notice = dialog;
        dialog.Show();
    }

    /// <summary>Closes the notice if it is open (IRIS is quitting). Does nothing, and never throws, when it is not open or already closing.</summary>
    public static void CloseNotice()
    {
        var dialog = _notice;
        _notice = null;
        if (dialog is null)
            return;
        try
        {
            dialog.Close();
        }
        catch (InvalidOperationException)
        {
            // already closing or closed
        }
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        if (_modeless)
        {
            Close(); // DialogResult can only be set on a window shown with ShowDialog
            return;
        }

        _yes = true;
        DialogResult = true; // closes the dialog
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _yes = false;
        DialogResult = false;
    }
}
