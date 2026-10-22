using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.App.Views;
using Iris.Core.Config;
using Iris.Core.Settings;

namespace Iris.App.Services;

/// <summary>
/// The opt-in automatic check of the user's hosted config address (setting "Check hosted config URL for updates every 24 hrs").
/// Runs in the background for as long as IRIS runs, with or without the config window.
///
/// - A timer wakes up every 10 minutes (and once at startup) and asks <see cref="HostedConfigSchedule.IsDue"/>: setting on, valid address, and
///   never checked or at least 24 hours since the last check.
/// - A check fetches the file with the shared <see cref="HostedConfigClient"/> (every request is logged, https and same-site redirects only).
///   The time is stored in settings.toml (hosted_config_last_check) after every attempt, successful or not; a failure is only logged.
/// - Same hash as the last imported file: nothing happens. Otherwise the user is asked "An update to your hosted config is available.
///   Do you want to update now?". Yes starts the normal import (checks, Backup/Merge/Replace question, fill-missing refresh, hash stored on
///   success). No does nothing more until the next 24-hour check, which asks again if the file still differs.
/// - It never starts while an import or another question is open, or while IRIS quits: that round is skipped, without storing the time,
///   so the next wake tries again.
/// The question follows the pop-up policy (<see cref="DialogHelper.PrepareBackgroundDialog"/>): never forced over another program.
/// Everything here runs on the UI thread.
/// </summary>
public sealed class HostedConfigAutoChecker : IDisposable
{
    private const string Headline = "An update to your hosted config is available.";
    private const string Question = Headline + " Do you want to update now?";

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly DispatcherTimer _timer;

    private bool _running;
    private bool _disposed;

    // The time of the last attempt of THIS run of IRIS. A second guard besides the stored time: if the stored time cannot be changed
    // (settings.toml unwritable), the address is still not fetched every ten minutes.
    private DateTimeOffset? _lastAttempt;

    // An update that was found while IRIS was busy: asked again at the next free wake without fetching the file again.
    // Dropped when the stored address no longer matches (or when the file turns out to be imported already).
    private PendingUpdate? _pending;

    private sealed record PendingUpdate(string Url, string Text, string Hash);

    public HostedConfigAutoChecker(AppServices services, ImportCoordinator imports)
    {
        _services = services;
        _imports = imports;
        _timer = new DispatcherTimer { Interval = HostedConfigSchedule.WakeInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>Starts the timer and makes the check at startup (if one is due).</summary>
    public void Start()
    {
        if (_disposed)
            return;
        _timer.Start();
        Wake();
    }

    /// <summary>Stops the timer. A check that is already running ends quietly (its fetch is cancelled when IRIS quits).</summary>
    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => Wake();

    private bool CanRun() => !_disposed && !_services.IsShuttingDown && !_imports.IsQuitting;

    /// <summary>True when no import, question or window operation is open that a new question would disturb.</summary>
    private bool IsFree()
    {
        if (_imports.IsBusy)
            return false;
        var host = _imports.Host;
        return host is null || host.IsIdleForPrompt;
    }

    private async void Wake()
    {
        if (_running || !CanRun())
            return;

        var now = _services.Clock.UtcNow;
        var settings = _services.Settings;
        if (!HostedConfigSchedule.IsDue(settings, now))
            return;
        if (_lastAttempt is { } last && now >= last && now - last < HostedConfigSchedule.Interval)
            return;
        if (!IsFree())
            return; // skip this round; the next wake tries again

        _running = true;
        try
        {
            await CheckAsync(settings.HostedConfigUrl);
        }
        catch (OperationCanceledException)
        {
            // IRIS is quitting
        }
        catch (Exception ex)
        {
            Log("Hosted config auto-check failed unexpectedly: " + ex);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task CheckAsync(string url)
    {
        string text;
        string hash;
        var current = _services.Settings;

        if (_pending is { } pending && string.Equals(pending.Url, url, StringComparison.Ordinal))
        {
            // Found earlier, but IRIS was busy then: ask again about the same file, without downloading it again.
            Log("Hosted config auto-check: asking again about the update found earlier (not fetched again).");
            text = pending.Text;
            hash = pending.Hash;
        }
        else
        {
            _pending = null; // none, or for another address
            Log("Hosted config auto-check: looking for an update.");
            var fetched = await _services.FetchHostedConfigAsync(url); // logs the request itself; throws OperationCanceledException when IRIS quits

            if (!CanRun())
                return;

            // The user may have changed the address or switched the check off while the file was coming in: this answer no longer counts.
            current = _services.Settings;
            if (!current.HostedConfigAutoCheck || !string.Equals(current.HostedConfigUrl, url, StringComparison.Ordinal))
            {
                Log("Hosted config auto-check: the setting changed meanwhile; the answer was dropped.");
                return;
            }

            if (!fetched.Success || fetched.Text is null || fetched.Hash is null)
            {
                Log("Hosted config auto-check failed: " + fetched.Error);
                RememberAttempt();
                return;
            }

            text = fetched.Text;
            hash = fetched.Hash;
        }

        if (!HostedConfigSchedule.IsUpdate(current, hash))
        {
            _pending = null;
            Log("Hosted config auto-check: no change since the last import.");
            RememberAttempt();
            return;
        }

        // An update. Asking needs a free moment; if there is none the round is skipped (nothing stored, so the next wake tries again,
        // and asks about this same file without fetching it again).
        if (!IsFree() || !_imports.TryBeginQuestion())
        {
            _pending = new PendingUpdate(url, text, hash);
            Log("Hosted config auto-check: an update is available, but IRIS is busy with another question; will ask again later.");
            return;
        }

        _pending = null;

        bool yes;
        try
        {
            Log("Hosted config auto-check: an update is available; asking the user.");
            RememberAttempt(); // stored before asking: a "No" (or a crash) means the next question comes after the next 24 hours
            yes = PromptDialog.Ask(Question, Headline, _services.Notify, _imports.Host?.DialogOwner);
        }
        finally
        {
            _imports.EndQuestion();
        }

        if (!yes)
        {
            Log("Hosted config auto-check: the user chose not to update now.");
            return;
        }

        await ImportAsync(url, text, hash);
    }

    /// <summary>The same import as the "Import from URL" button: checks, Backup/Merge/Replace question, fill-missing refresh, hash stored on success.</summary>
    private async Task ImportAsync(string url, string text, string hash)
    {
        if (!CanRun())
            return;

        // The user just said Yes, so the dialogs that follow are shown normally (userJustActed). An address typed in the Settings window but not
        // saved is an ordinary unsaved change here (ignoreHostedUrl: false): the user gets the save/discard/cancel question, nothing is silently dropped.
        var attempt = await _imports.ImportTextAsync(text, "Import from " + HostOf(url), ignoreHostedUrl: false, userJustActed: true);
        if (attempt.Outcome != ImportOutcome.Imported)
        {
            Log("Hosted config auto-check: not imported (" + attempt.Outcome + "): " + attempt.Message);
            if (attempt.Outcome != ImportOutcome.Cancelled)
                _services.Notify?.Invoke(attempt.Message);
            return;
        }

        // Remember this version, so neither the next check nor "Import from URL" offers it again. Only for the address that was fetched.
        var message = attempt.Message;
        if (string.Equals(_services.Settings.HostedConfigUrl, url, StringComparison.Ordinal))
        {
            var remembered = _services.UpdateSettings(s => s with { HostedConfigLastHash = hash });
            if (!remembered.Success)
                message += " (the version could not be saved: " + remembered.Error + ")";
        }
        _services.Notify?.Invoke(message);
    }

    /// <summary>Stores the time of this attempt (UTC, whole seconds). A failure to store it is only logged.</summary>
    private void RememberAttempt()
    {
        var stamp = AppSettingsToml.TruncateToSeconds(_services.Clock.UtcNow);
        _lastAttempt = stamp;
        var saved = _services.UpdateSettings(s => s with { HostedConfigLastCheck = stamp });
        if (!saved.Success)
            Log("Hosted config auto-check: the time of this check could not be saved: " + saved.Error);
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void Log(string message) => _services.Log.Write(_services.Clock.UtcNow, message);
}
