using System;
using System.Windows;

namespace Iris.App.Views;

/// <summary>
/// IRIS's own small message box ("IRIS" as the title, a message, Yes/No or OK). It exists so that a question that appears on its own can be
/// shown without taking the focus away from the game: <see cref="DialogHelper.PrepareBackgroundDialog"/> decides how it is shown.
/// Use <see cref="Ask"/> for a Yes/No question, <see cref="Tell"/> for a message with OK and <see cref="TellWithClose"/> for a message with Close.
/// Closing the window counts as No. <see cref="IsAnyOpen"/> says whether one of these pop-ups is on screen.
/// </summary>
public partial class PromptDialog : Window
{
    private static int _openCount; // only touched on the UI thread

    private bool _yes;

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

    /// <summary>Shows a message with an OK button.</summary>
    public static void Tell(string message, string balloonText, Action<string>? notify, Window? owner, bool userJustActed = false)
    {
        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify, userJustActed);
        ShowCounted(dialog);
    }

    /// <summary>
    /// Shows a message with a single Close button and no tray balloon, following the pop-up policy: with another program in front (the game)
    /// it is not activated and waits in the taskbar; if IRIS is in front it is shown normally. Returns when the user closes it.
    /// </summary>
    public static void TellWithClose(string message, Window? owner)
    {
        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true, okText: "Close");
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText: string.Empty, notify: null);
        ShowCounted(dialog);
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        _yes = true;
        DialogResult = true; // closes the dialog
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _yes = false;
        DialogResult = false;
    }
}
