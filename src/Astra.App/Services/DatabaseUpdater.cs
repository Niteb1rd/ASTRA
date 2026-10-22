using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Astra.App.Services;

/// <summary>
/// "Update database now", shared by the tray menu and the Settings tab so both do exactly the same thing:
/// first the hosted config check (only when an address is set), then the RSI refresh with the possibly new config.
/// Only one update runs at a time; <see cref="RunningChanged"/> lets the tray item and the button grey themselves out.
/// Call it from the UI thread (it shows dialogs when the hosted config has changed).
/// </summary>
public sealed class DatabaseUpdater
{
    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;

    public DatabaseUpdater(AppServices services, ImportCoordinator imports)
    {
        _services = services;
        _imports = imports;
    }

    /// <summary>True from the start of an update until it ends.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsRunning"/> changes.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>
    /// Runs the update and returns one short paragraph for the result balloon or status bar.
    /// Returns null when an update is already running, or when ASTRA is quitting. Never throws.
    /// </summary>
    public async Task<string?> RunAsync()
    {
        if (IsRunning)
            return null;

        SetRunning(true);
        try
        {
            var parts = new List<string>();

            // A hosted-config problem is mentioned, but the RSI refresh goes on regardless.
            var hosted = await _imports.CheckHostedConfigAsync();
            if (!string.IsNullOrEmpty(hosted))
                parts.Add(hosted);

            parts.Add(await _services.UpdateDatabaseNowAsync());

            var message = string.Join(" ", parts);
            _services.Log.Write(_services.Clock.UtcNow, "Update database now: " + message);
            return message;
        }
        catch (OperationCanceledException)
        {
            return null; // ASTRA is quitting
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Update database now failed: " + ex.Message);
            return "The database update failed: " + ex.Message;
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        IsRunning = running;
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }
}
