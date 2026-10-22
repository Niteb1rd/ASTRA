using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Views;
using Iris.Core.Config;

namespace Iris.App.Services;

/// <summary>What the import flow needs from the config window, if one is open.</summary>
public interface IImportHost
{
    /// <summary>The window that owns the dialogs.</summary>
    Window DialogOwner { get; }

    /// <summary>True when the window holds changes that are not saved yet. With <paramref name="ignoreHostedUrl"/> a changed hosted address alone does not count.</summary>
    bool HasUnsavedChanges(bool ignoreHostedUrl);

    /// <summary>
    /// Validates and saves the lists and settings in the window (without closing it). False when nothing was saved.
    /// With <paramref name="ignoreHostedUrl"/> the stored hosted address and version stay as they are (the caller stores the address
    /// itself, and only after a successful import), so an address typed but not yet used is not written.
    /// </summary>
    Task<bool> SaveChangesAsync(bool ignoreHostedUrl);

    /// <summary>True while an import, export or save is running in the window (it cannot be closed then).</summary>
    bool IsBusy { get; }

    /// <summary>
    /// True when nothing is going on in the window that a new question would disturb: no save, import or export, no question of its own open
    /// (for example "discard your changes?" when closing).
    /// </summary>
    bool IsIdleForPrompt { get; }

    /// <summary>Marks the window busy (it cannot be closed, import buttons are off). Calls must be paired with <see cref="EndBusy"/>.</summary>
    void BeginBusy();

    void EndBusy();

    /// <summary>The app's config was replaced by an import: show the new rows and settings, check every name, and show <paramref name="status"/>.</summary>
    void ReloadAfterImport(string status);
}

/// <summary>The result of <see cref="ImportCoordinator.ImportTextAsync"/>.</summary>
public sealed record ImportAttempt(ImportOutcome Outcome, string Message);

/// <summary>
/// The whole "bring in a config" flow, used by the Import buttons of the Settings tab (file and URL):
/// check the text, ask about warnings, ask to save unsaved window changes, ask which mode, write the file, update the app.
/// After the file was written <see cref="AppServices.ApplyImport"/> raises <see cref="AppServices.ListApplied"/>, which starts the
/// background fill-missing refresh; an import never runs the full refresh.
/// Everything here runs on the UI thread (dialogs); the entry points hop onto it if they are called from another thread.
/// Only one import runs at a time (<see cref="IsBusy"/>).
/// If the config window is open it is the <see cref="Host"/> (it reloads its rows); if not, the file and the app's config are updated directly.
/// </summary>
public sealed class ImportCoordinator
{
    private const int MaxListed = 15;

    private readonly AppServices _services;

    // True from the start of an import until it ends, dialogs included. Only touched on the UI thread.
    private bool _active;

    // Questions IRIS asks on its own (the 24-hour hosted config update question), see TryBeginQuestion. Only touched on the UI thread.
    private int _questions;

    public ImportCoordinator(AppServices services)
    {
        _services = services;
    }

    /// <summary>The open config window, or null. Set by the window itself.</summary>
    public IImportHost? Host { get; set; }

    /// <summary>
    /// True while an import is running, including any question it has open, or while a question IRIS asked on its own is open
    /// (<see cref="TryBeginQuestion"/>). While it is true no second import starts, the Settings
    /// window is not opened and IRIS is not quit (the services must not go away under an open dialog).
    /// </summary>
    public bool IsBusy => _active || _questions > 0;

    /// <summary>
    /// Claims the right to ask the user something on IRIS's own initiative (the automatic hosted config check). False, and nothing claimed, when
    /// an import or another question is open or IRIS is quitting: the caller skips this round. Pair a true result with <see cref="EndQuestion"/>.
    /// UI thread only.
    /// </summary>
    public bool TryBeginQuestion()
    {
        if (IsQuitting || IsBusy)
            return false;
        _questions++;
        return true;
    }

    /// <summary>Ends a question claimed with <see cref="TryBeginQuestion"/>.</summary>
    public void EndQuestion()
    {
        if (_questions > 0)
            _questions--;
    }

    /// <summary>Set by the app when it starts quitting: no new import (or question) starts after that.</summary>
    public bool IsQuitting { get; set; }

    /// <summary>
    /// Imports <paramref name="text"/> (the content of a config file). The text is only ever parsed as data.
    /// </summary>
    /// <param name="text">The file content.</param>
    /// <param name="headline">Heading of the choice dialog.</param>
    /// <param name="ignoreHostedUrl">True when the caller is about to store the address itself, so an edited address box is not an "unsaved change".</param>
    /// <param name="userJustActed">True when the import follows something the user just did (a button, a Yes to the update question): the
    /// dialogs that have no config window to belong to are then shown normally, even if IRIS is not in front, and no balloon is shown.</param>
    public async Task<ImportAttempt> ImportTextAsync(string text, string headline, bool ignoreHostedUrl, bool userJustActed = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var inner = await dispatcher.InvokeAsync<Task<ImportAttempt>>(
                () => ImportTextAsync(text, headline, ignoreHostedUrl, userJustActed)).Task;
            return await inner;
        }

        if (IsQuitting)
            return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled: IRIS is quitting.");
        if (_active || _questions > 0)
            return new ImportAttempt(ImportOutcome.Busy, "Another import is already running.");

        _active = true;
        var host = Host;
        var owner = host?.DialogOwner;
        try
        {
            host?.BeginBusy();

            // 1. Parse. Errors stop everything; notes let the user decide.
            var read = ConfigImporter.ParseForImport(text);
            if (!read.IsValid || read.Config is null)
            {
                Tell(owner, "This config cannot be imported. Nothing was changed.\n\n" + BulletList(read.Errors), MessageBoxImage.Warning,
                    headline, userJustActed);
                return new ImportAttempt(ImportOutcome.Rejected, "Import failed: the config has problems.");
            }

            if (read.Warnings.Count > 0)
            {
                var answer = Ask(owner,
                    "The config has some notes:\n\n" + BulletList(read.Warnings) + "\n\nContinue with the import?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, headline, userJustActed);
                if (answer != MessageBoxResult.Yes)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
            }

            // 2. Unsaved changes in the window: save them, throw them away, or stop.
            if (host is not null && host.HasUnsavedChanges(ignoreHostedUrl))
            {
                var answer = Ask(owner,
                    "You have unsaved changes in the Settings window.\n\n"
                    + "Yes: save them, then import.\n"
                    + "No: discard your changes and import.\n"
                    + "Cancel: stop; nothing is imported.",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel, headline, userJustActed);
                if (answer != MessageBoxResult.Yes && answer != MessageBoxResult.No)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
                if (answer == MessageBoxResult.Yes && !await host.SaveChangesAsync(ignoreHostedUrl))
                    return new ImportAttempt(ImportOutcome.NotSaved, "Import cancelled: your changes were not saved.");
            }

            // 3. Which mode? (Asked after the save above, because saving may have just created the list file.)
            var hasExisting = File.Exists(_services.Paths.SocialListFile);
            var mode = ImportChoiceDialog.Ask(owner, read.Config, hasExisting, headline, showDeclineNote: false, _services.Notify, userJustActed);
            if (mode is null)
                return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");

            // 4. Write the file and tell the app.
            var result = _services.ApplyImport(read.Config, mode.Value);
            if (!result.Success)
            {
                Tell(owner, "The config was not imported. Nothing was changed.\n\n" + result.Error, MessageBoxImage.Error, headline, userJustActed);
                return new ImportAttempt(ImportOutcome.WriteFailed, "Import failed: " + result.Error);
            }

            // The window that is open NOW (not necessarily the one from the start) shows the new rows.
            Host?.ReloadAfterImport(result.Summary);
            return new ImportAttempt(ImportOutcome.Imported, result.Summary);
        }
        finally
        {
            host?.EndBusy();
            _active = false;
        }
    }

    // ---------------------------------------------------------------------------------------

    private static string BulletList(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines.Take(MaxListed))
            text.AppendLine("  - " + line);
        if (lines.Count > MaxListed)
            text.AppendLine($"  ...and {lines.Count - MaxListed} more.");
        return text.ToString().TrimEnd();
    }

    private void Tell(Window? owner, string text, MessageBoxImage icon, string balloonText, bool userJustActed) =>
        Ask(owner, text, MessageBoxButton.OK, icon, MessageBoxResult.OK, balloonText, userJustActed);

    /// <summary>
    /// Asks with the config window as owner when there is one (brought to the front first, so a minimized window does not hide
    /// the question): an ordinary message box. Without a window, an OK message and a Yes/No question use IRIS's own
    /// <see cref="PromptDialog"/>, which follows the pop-up policy (<see cref="DialogHelper.PrepareBackgroundDialog"/>: never forced over
    /// the game unless <paramref name="userJustActed"/>). Only the ownerless Yes/No/Cancel question is a plain message box.
    /// </summary>
    private MessageBoxResult Ask(
        Window? owner, string text, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult,
        string balloonText, bool userJustActed)
    {
        if (owner is not null && DialogHelper.PrepareOwner(owner))
            return MessageBox.Show(owner, text, "IRIS", buttons, icon, defaultResult);

        switch (buttons)
        {
            case MessageBoxButton.OK:
                PromptDialog.Tell(text, balloonText, _services.Notify, owner: null, userJustActed: userJustActed);
                return MessageBoxResult.OK;
            case MessageBoxButton.YesNo:
                return PromptDialog.Ask(
                    text, balloonText, _services.Notify, owner: null, defaultYes: defaultResult == MessageBoxResult.Yes, userJustActed: userJustActed)
                    ? MessageBoxResult.Yes
                    : MessageBoxResult.No;
            default:
                return MessageBox.Show(text, "IRIS", buttons, icon, defaultResult);
        }
    }
}
