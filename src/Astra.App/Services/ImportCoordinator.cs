using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Astra.App.Views;
using Astra.Core.Config;

namespace Astra.App.Services;

/// <summary>What the import flow needs from the config window, if one is open.</summary>
public interface IImportHost
{
    /// <summary>The window that owns the dialogs.</summary>
    Window DialogOwner { get; }

    /// <summary>True when the window holds changes that are not saved yet. With <paramref name="ignoreHostedUrl"/> a changed hosted address alone does not count.</summary>
    bool HasUnsavedChanges(bool ignoreHostedUrl);

    /// <summary>Validates and saves the lists and settings in the window (without closing it). False when nothing was saved.</summary>
    Task<bool> SaveChangesAsync();

    /// <summary>Marks the window busy (it cannot be closed, import buttons are off). Calls must be paired with <see cref="EndBusy"/>.</summary>
    void BeginBusy();

    void EndBusy();

    /// <summary>The app's config was replaced by an import: show the new rows and settings, check every name, and show <paramref name="status"/>.</summary>
    void ReloadAfterImport(string status);
}

/// <summary>How an import attempt ended.</summary>
public enum ImportOutcome
{
    /// <summary>The file list was changed.</summary>
    Imported,

    /// <summary>The user said no (or closed a dialog); nothing was changed.</summary>
    Cancelled,

    /// <summary>The text had errors, or writing failed; nothing was changed. The user has been told.</summary>
    Failed,
}

/// <summary>The result of <see cref="ImportCoordinator.ImportTextAsync"/>.</summary>
public sealed record ImportAttempt(ImportOutcome Outcome, string Message);

/// <summary>
/// The whole "bring in a config" flow, used by the Settings tab buttons and by the hosted-config check of "Update database now":
/// check the text, ask about warnings, ask to save unsaved window changes, ask which mode, write the file, update the app.
/// Everything here runs on the UI thread (dialogs); the entry points hop onto it if they are called from another thread.
/// If the config window is open it is the <see cref="Host"/> (it reloads its rows); if not, the file and the app's config are updated directly.
/// </summary>
public sealed class ImportCoordinator
{
    private const int MaxListed = 15;

    private readonly AppServices _services;

    public ImportCoordinator(AppServices services)
    {
        _services = services;
    }

    /// <summary>The open config window, or null. Set by the window itself.</summary>
    public IImportHost? Host { get; set; }

    /// <summary>
    /// Imports <paramref name="text"/> (the content of a config file). The text is only ever parsed as data.
    /// </summary>
    /// <param name="text">The file content.</param>
    /// <param name="headline">Heading of the choice dialog.</param>
    /// <param name="showDeclineNote">True for the hosted config: the dialog says that Cancel is remembered.</param>
    /// <param name="ignoreHostedUrl">True when the caller is about to store the address itself, so an edited address box is not an "unsaved change".</param>
    public async Task<ImportAttempt> ImportTextAsync(string text, string headline, bool showDeclineNote, bool ignoreHostedUrl)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var inner = await dispatcher.InvokeAsync<Task<ImportAttempt>>(
                () => ImportTextAsync(text, headline, showDeclineNote, ignoreHostedUrl)).Task;
            return await inner;
        }

        var host = Host;
        var owner = host?.DialogOwner;

        host?.BeginBusy();
        try
        {
            // 1. Parse. Errors stop everything; notes let the user decide.
            var read = SocialConfigToml.Read(text);
            if (!read.IsValid || read.Config is null)
            {
                Tell(owner, "This config cannot be imported. Nothing was changed.\n\n" + BulletList(read.Errors), MessageBoxImage.Warning);
                return new ImportAttempt(ImportOutcome.Failed, "Import failed: the config has problems.");
            }

            if (read.Warnings.Count > 0)
            {
                var answer = Ask(owner,
                    "The config has some notes:\n\n" + BulletList(read.Warnings) + "\n\nContinue with the import?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
            }

            // 2. Unsaved changes in the window: save them, throw them away, or stop.
            if (host is not null && host.HasUnsavedChanges(ignoreHostedUrl))
            {
                var answer = Ask(owner, "Save your changes first?",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel)
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");
                if (answer == MessageBoxResult.Yes && !await host.SaveChangesAsync())
                    return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled: your changes were not saved.");
            }

            // 3. Which mode? (Asked after the save above, because saving may have just created the list file.)
            var hasExisting = File.Exists(_services.Paths.SocialListFile);
            var mode = ImportChoiceDialog.Ask(owner, read.Config, hasExisting, headline, showDeclineNote);
            if (mode is null)
                return new ImportAttempt(ImportOutcome.Cancelled, "Import cancelled.");

            // 4. Write the file and tell the app.
            var result = _services.ApplyImport(read.Config, mode.Value);
            if (!result.Success)
            {
                Tell(owner, "The config was not imported. Nothing was changed.\n\n" + result.Error, MessageBoxImage.Error);
                return new ImportAttempt(ImportOutcome.Failed, "Import failed: " + result.Error);
            }

            host?.ReloadAfterImport(result.Summary);
            return new ImportAttempt(ImportOutcome.Imported, result.Summary);
        }
        finally
        {
            host?.EndBusy();
        }
    }

    /// <summary>
    /// The first half of "Update database now": when a hosted config address is set, fetch it, and if it is a version ASTRA has not
    /// applied or declined yet, offer it through the normal import flow. Whatever the user decides, the version's hash is stored so the
    /// same version is never offered twice. Returns a short note for the result message, or null when there is nothing to report.
    /// Problems become the note; they never stop the caller (the RSI refresh goes on).
    /// </summary>
    public async Task<string?> CheckHostedConfigAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var inner = await dispatcher.InvokeAsync<Task<string?>>(CheckHostedConfigAsync).Task;
            return await inner;
        }

        var url = _services.Settings.HostedConfigUrl;
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var fetched = await _services.FetchHostedConfigAsync(url);
            if (!fetched.Success || fetched.Text is null || fetched.Hash is null)
            {
                _services.Log.Write(_services.Clock.UtcNow, "Hosted config not fetched: " + fetched.Error);
                return "Hosted config not fetched: " + fetched.Error;
            }

            if (string.Equals(fetched.Hash, _services.Settings.HostedConfigLastHash, StringComparison.OrdinalIgnoreCase))
            {
                _services.Log.Write(_services.Clock.UtcNow, "Hosted config unchanged since the last time.");
                return null;
            }

            var attempt = await ImportTextAsync(fetched.Text, "Your hosted config has changed", showDeclineNote: true, ignoreHostedUrl: false);

            // Applied, declined and rejected versions are all remembered: the user has seen this version.
            var hash = fetched.Hash;
            var saved = _services.UpdateSettings(s => s with { HostedConfigLastHash = hash });
            if (!saved.Success)
                _services.Log.Write(_services.Clock.UtcNow, "Could not remember the hosted config version: " + saved.Error);

            return attempt.Outcome switch
            {
                ImportOutcome.Imported => "Hosted config imported. " + attempt.Message,
                ImportOutcome.Cancelled => "Hosted config change skipped.",
                _ => "Hosted config was not imported (it has problems).",
            };
        }
        catch (OperationCanceledException)
        {
            throw; // ASTRA is quitting
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Hosted config check failed: " + ex);
            return "Hosted config check failed: " + ex.Message;
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

    private static void Tell(Window? owner, string text, MessageBoxImage icon) =>
        Ask(owner, text, MessageBoxButton.OK, icon, MessageBoxResult.OK);

    private static MessageBoxResult Ask(
        Window? owner, string text, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        owner is not null
            ? MessageBox.Show(owner, text, "ASTRA", buttons, icon, defaultResult)
            : MessageBox.Show(text, "ASTRA", buttons, icon, defaultResult);
}
