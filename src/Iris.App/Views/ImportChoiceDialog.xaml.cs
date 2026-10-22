using System;
using System.Windows;
using Iris.Core.Config;

namespace Iris.App.Views;

/// <summary>
/// Asks how an imported config should be applied: back up the current file, merge, or replace.
/// Shows a short summary of the imported file first. When there is no list file yet it only asks for a confirmation.
/// Use <see cref="Ask"/>; it returns null when the user cancels.
/// </summary>
public partial class ImportChoiceDialog : Window
{
    private ImportMode? _choice;

    private ImportChoiceDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows the dialog and returns the chosen mode, or null for Cancel.</summary>
    /// <param name="owner">The window to center on, or null for the middle of the screen. Without an owner the pop-up policy applies
    /// (<see cref="DialogHelper.PrepareBackgroundDialog"/>): if another program is in front, the dialog waits in the taskbar and a tray balloon says so.</param>
    /// <param name="imported">The parsed file, for the summary line.</param>
    /// <param name="hasExistingFile">False when there is no list file yet (no choice to make).</param>
    /// <param name="headline">Heading text, e.g. "Your hosted config has changed".</param>
    /// <param name="showDeclineNote">True for the hosted config: tells the user that Cancel is remembered.</param>
    /// <param name="notify">Shows a tray balloon (used with the headline when the dialog has to wait behind another program); may be null.</param>
    public static ImportMode? Ask(
        Window? owner, SocialConfig imported, bool hasExistingFile, string headline, bool showDeclineNote, Action<string>? notify = null)
    {
        var dialog = new ImportChoiceDialog();
        dialog.HeadlineText.Text = headline;
        dialog.SummaryText.Text = Describe(imported);
        dialog.ChoicesPanel.Visibility = hasExistingFile ? Visibility.Visible : Visibility.Collapsed;
        dialog.NoExistingPanel.Visibility = hasExistingFile ? Visibility.Collapsed : Visibility.Visible;
        dialog.DeclineNote.Visibility = showDeclineNote ? Visibility.Visible : Visibility.Collapsed;

        if (owner is not null && DialogHelper.PrepareOwner(owner))
        {
            // The owner is restored (if it was minimized) and in front, so the dialog is seen.
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            // No window to belong to (or it is gone): the dialog stands on its own, with a taskbar button. It is never forced on top of
            // another program: if IRIS is not in front it is shown without taking the focus, and the balloon says that it is waiting.
            DialogHelper.PrepareBackgroundDialog(dialog, null, headline, notify);
        }

        dialog.ShowDialog();
        return dialog._choice;
    }

    private static string Describe(SocialConfig config)
    {
        var name = string.IsNullOrWhiteSpace(config.Name) ? "(no name)" : config.Name;
        return $"List name: {name}\n"
            + $"{Plural(config.Players.Count, "player", "players")}, "
            + $"{Plural(config.Orgs.Count, "org", "orgs")}, "
            + $"{Plural(config.UserRuleCount, "org-attribute rule", "org-attribute rules")}";
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private void OnBackup(object sender, RoutedEventArgs e) => Choose(ImportMode.BackupExisting);

    private void OnMerge(object sender, RoutedEventArgs e) => Choose(ImportMode.Merge);

    private void OnReplace(object sender, RoutedEventArgs e) => Choose(ImportMode.Replace);

    private void Choose(ImportMode mode)
    {
        _choice = mode;
        DialogResult = true; // closes the dialog
    }
}
