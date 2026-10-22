using System;
using System.Windows;

namespace Iris.App.Views;

/// <summary>
/// IRIS's own small message box ("IRIS" as the title, a message, Yes/No or OK). It exists so that a question that appears on its own can be
/// shown without taking the focus away from the game: <see cref="DialogHelper.PrepareBackgroundDialog"/> decides how it is shown.
/// Use <see cref="Ask"/> for a Yes/No question and <see cref="Tell"/> for a message with OK. Closing the window counts as No.
/// </summary>
public partial class PromptDialog : Window
{
    private bool _yes;

    private PromptDialog(string message, bool yesNo, bool defaultYes)
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
            YesButton.Content = "OK";
            YesButton.IsCancel = true; // Escape closes it
            NoButton.Visibility = Visibility.Collapsed;
            NoButton.IsCancel = false;
        }
    }

    /// <summary>
    /// Asks a Yes/No question. True only for Yes.
    /// </summary>
    /// <param name="message">The question.</param>
    /// <param name="balloonText">Tray balloon shown when the question has to wait in the background (another program is in front).</param>
    /// <param name="notify">Shows a tray balloon; may be null.</param>
    /// <param name="owner">The config window if it is open (used only when IRIS is the foreground program), else null.</param>
    public static bool Ask(string message, string balloonText, Action<string>? notify, Window? owner, bool defaultYes = true)
    {
        var dialog = new PromptDialog(message, yesNo: true, defaultYes);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify);
        dialog.ShowDialog();
        return dialog._yes;
    }

    /// <summary>Shows a message with an OK button.</summary>
    public static void Tell(string message, string balloonText, Action<string>? notify, Window? owner)
    {
        var dialog = new PromptDialog(message, yesNo: false, defaultYes: true);
        DialogHelper.PrepareBackgroundDialog(dialog, owner, balloonText, notify);
        dialog.ShowDialog();
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
