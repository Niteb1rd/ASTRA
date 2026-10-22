using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Astra.App.Services;
using Astra.App.Views;

namespace Astra.App;

/// <summary>
/// Starts ASTRA: one instance only, opens the data under the "Data" folder next to the exe, shows the tray icon.
/// The app lives in the tray; closing the config window does not end it (ShutdownMode is OnExplicitShutdown),
/// only the tray menu's Quit does.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "ASTRA.SingleInstance";

    private Mutex? _mutex;
    private AppServices? _services;
    private TrayIcon? _tray;
    private ConfigWindow? _configWindow;
    private ImportCoordinator? _imports;
    private DatabaseUpdater? _updater;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show(
                "ASTRA is already running. Look for its icon in the system tray (bottom right, maybe under the ^ arrow).",
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            StartUp();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "ASTRA could not start:\n\n" + ex.Message + "\n\nData folder: " + Path.Combine(AppContext.BaseDirectory, "Data"),
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); // OnExit releases the mutex and removes the tray icon
        }
    }

    /// <summary>Everything that happens once this is the only running instance.</summary>
    private void StartUp()
    {
        var services = AppServices.Create(AppContext.BaseDirectory);
        _services = services;

        DispatcherUnhandledException += OnUnhandledException;

        var imports = new ImportCoordinator(services);
        var updater = new DatabaseUpdater(services, imports);
        _imports = imports;
        _updater = updater;

        var tray = new TrayIcon();
        _tray = tray;
        updater.RunningChanged += (_, _) => tray.SetUpdateRunning(updater.IsRunning);
        tray.SettingsRequested += (_, _) => OpenSettings();
        tray.UpdateDatabaseRequested += (_, _) => StartDatabaseUpdate();
        tray.PauseChanged += (_, paused) => services.IsOverlayPaused = paused;
        tray.OpenLogRequested += (_, _) => OpenLog();
        tray.OpenConfigLocationRequested += (_, _) => OpenConfigLocation();
        tray.QuitRequested += (_, _) => Quit();

        var load = services.ListLoad;
        if (!load.IsValid)
            ShowListProblems(load.Errors.ToList(), load.Warnings.ToList()); // a dialog: the user has to act

        // The dialog above is modal; the user may have chosen Quit in the tray meanwhile.
        if (_shuttingDown)
            return;

        if (load.IsValid)
        {
            if (load.Warnings.Count > 0)
            {
                // Notes only (for example keys from a newer version): worth a mention, not a dialog.
                var extra = load.Warnings.Count > 1 ? $" (and {load.Warnings.Count - 1} more)" : string.Empty;
                _tray?.ShowBalloon("ASTRA is running in the system tray. Note about your list file: " + load.Warnings[0] + extra);
            }
            else
            {
                _tray?.ShowBalloon("ASTRA is running in the system tray. Right-click the icon for Settings.");
            }
        }
        else
        {
            OpenSettings(); // let the user fix the list; the file itself is untouched until they press Save
        }

        // A settings file that could not be read is the one thing worth a balloon of its own (it replaces the greeting).
        var settingsLoad = services.SettingsLoad;
        if (!settingsLoad.IsValid)
            _tray?.ShowBalloon("Could not read Data\\settings.toml (" + settingsLoad.Errors[0] + "). Default settings are used until you save in the Settings tab.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        _services?.Dispose();
        _services = null;

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not owned by this thread: the system frees it when the process ends
            }
            _mutex.Dispose();
            _mutex = null;
        }

        base.OnExit(e);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Opens the config window, or brings the open one to the front (only one at a time).</summary>
    private void OpenSettings()
    {
        if (_services is null || _shuttingDown)
            return;

        if (_configWindow is not null)
        {
            if (_configWindow.WindowState == WindowState.Minimized)
                _configWindow.WindowState = WindowState.Normal;
            _configWindow.Activate();
            return;
        }

        var window = new ConfigWindow(_services, _imports!, _updater!);
        window.Closed += (_, _) => _configWindow = null;
        _configWindow = window;
        window.Show();
        window.Activate();
    }

    private async void StartDatabaseUpdate()
    {
        var updater = _updater;
        if (updater is null || _tray is null || _shuttingDown)
            return;

        var message = await updater.RunAsync(); // null: already running, or ASTRA is quitting
        if (message is not null)
            _tray?.ShowBalloon(message);
    }

    /// <summary>Opens today's log file in the default viewer (Notepad if .log has no program).</summary>
    private void OpenLog()
    {
        var services = _services;
        if (services is null || _shuttingDown)
            return;

        try
        {
            var path = services.FileLog.CurrentFilePath;
            if (!File.Exists(path))
                services.Log.Write(DateTimeOffset.UtcNow, "Log opened");
            if (!File.Exists(path))
                File.WriteAllBytes(path, Array.Empty<byte>()); // logging failed (for example no folder): still give Notepad a file

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // no program is associated with .log files
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Could not open the log: " + ex.Message);
        }
    }

    /// <summary>Opens Windows Explorer at the Data folder (config, cache, exclusions, logs).</summary>
    private void OpenConfigLocation()
    {
        var services = _services;
        if (services is null || _shuttingDown)
            return;

        try
        {
            var folder = services.Paths.DataFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Could not open the config location: " + ex.Message);
        }
    }

    private void Quit()
    {
        var window = _configWindow;
        if (window is not null)
        {
            try
            {
                window.Close(); // asks about unsaved changes; the user may say no
            }
            catch (InvalidOperationException)
            {
                // the window is already closing (for example its "discard changes?" question is open): do not quit
                window.Activate();
                return;
            }
            if (_configWindow is not null)
                return;
        }

        _shuttingDown = true;
        Shutdown();
    }

    private static void ShowListProblems(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        var text = new StringBuilder();
        if (errors.Count > 0)
        {
            text.AppendLine("ASTRA found problems in your list file (Data\\social-overlay.toml):");
            text.AppendLine();
            foreach (var error in errors.Take(12))
                text.AppendLine("  - " + error);
            if (errors.Count > 12)
                text.AppendLine($"  ...and {errors.Count - 12} more.");
            text.AppendLine();
            text.AppendLine("The file has not been changed. Entries with problems are left out of the Settings window; nothing is written until you press Save.");
        }
        if (warnings.Count > 0)
        {
            if (errors.Count > 0)
                text.AppendLine();
            text.AppendLine("Notes:");
            foreach (var warning in warnings.Take(8))
                text.AppendLine("  - " + warning);
            if (warnings.Count > 8)
                text.AppendLine($"  ...and {warnings.Count - 8} more.");
        }

        MessageBox.Show(text.ToString(), "ASTRA", MessageBoxButton.OK,
            errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Write(DateTimeOffset.UtcNow, "Unexpected error: " + e.Exception);
        MessageBox.Show("Something unexpected went wrong:\n\n" + e.Exception.Message + "\n\nASTRA keeps running.",
            "ASTRA", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
