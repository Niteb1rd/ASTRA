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
using Iris.App.Services;
using Iris.App.Views;
using Iris.Core.Storage;

namespace Iris.App;

/// <summary>
/// Starts IRIS: one instance only, opens the data under the "Data" folder next to the exe, shows the tray icon.
/// The app lives in the tray; closing the config window does not end it (ShutdownMode is OnExplicitShutdown),
/// only the tray menu's Quit does.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "IRIS.SingleInstance";

    private Mutex? _mutex;
    private AppServices? _services;
    private TrayIcon? _tray;
    private ConfigWindow? _configWindow;
    private ImportCoordinator? _imports;
    private CacheUpdater? _updater;
    private bool _shuttingDown;

    private const string AnswerFirstText = "Answer the open IRIS question first.";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show(
                "IRIS is already running. Look for its icon in the system tray (bottom right, maybe under the ^ arrow).",
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Information);
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
                "IRIS could not start:\n\n" + ex.Message + "\n\nData folder: " + Path.Combine(AppContext.BaseDirectory, "Data"),
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var updater = new CacheUpdater(services);
        _imports = imports;
        _updater = updater;

        var tray = new TrayIcon();
        _tray = tray;
        services.Notify = text => tray.ShowBalloon(text);
        updater.RefreshingChanged += (_, _) => tray.SetActivity(updater.IsRefreshing ? "refreshing the cache..." : null);
        updater.FillMissingFinished += (_, message) => ShowStatus(message);
        tray.SettingsRequested += (_, _) => OpenSettings();
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

        // Files with errors that IRIS could work around with the last good copy, and settings it could not read at all, are worth a
        // balloon of their own (they replace the greeting).
        var problems = new List<string>();
        if (services.ListStartup.UsedLastGood)
            problems.Add(ConfigMessages.UsingLastGood(DataPaths.SocialListFileName, services.ListStartup.LastGoodTime!.Value));
        if (services.SettingsStartup.UsedLastGood)
            problems.Add(ConfigMessages.UsingLastGood(DataPaths.SettingsFileName, services.SettingsStartup.LastGoodTime!.Value));
        else if (!services.SettingsLoad.IsValid)
            problems.Add("Could not read Data\\settings.toml (" + services.SettingsLoad.Errors[0] + "). Default settings are used until you save in the Settings tab.");

        if (problems.Count > 0)
        {
            // A pop-up, not a balloon: Windows can hide balloons (focus assist, full-screen game), and the user must know
            // that IRIS is not running on the file they edited.
            var answer = MessageBox.Show(
                "Errors were detected in your config files:\n\n- " + string.Join(".\n- ", problems) + ".\n\n"
                + "Your files were not changed. Open Settings to see the errors and fix them now?",
                "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (_shuttingDown)
                return;
            if (answer == MessageBoxResult.Yes && load.IsValid)
                OpenSettings();
        }
        else if (load.IsValid && load.Warnings.Count > 0)
        {
            // Notes only (for example keys from a newer version): worth a mention, not a dialog.
            var extra = load.Warnings.Count > 1 ? $" (and {load.Warnings.Count - 1} more)" : string.Empty;
            _tray?.ShowBalloon("IRIS is running in the system tray. Note about your list file: " + load.Warnings[0] + extra);
        }
        else if (load.IsValid)
        {
            _tray?.ShowBalloon("IRIS is running in the system tray. Right-click the icon for Settings.");
        }

        if (!load.IsValid)
            OpenSettings(); // let the user fix the list; the file itself is untouched until they press Save
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

        // An import is waiting for an answer (its question may be hidden behind other windows): no second window on top of it.
        if (_imports?.IsBusy == true)
        {
            _tray?.ShowBalloon(AnswerFirstText);
            return;
        }

        if (_configWindow is not null)
        {
            if (_configWindow.WindowState == WindowState.Minimized)
                _configWindow.WindowState = WindowState.Normal;
            _configWindow.Activate();
            return;
        }

        // The config files may have been edited while the window was closed. The window looks at them itself as soon as it is shown
        // (its first check runs at once, on a background thread, not after 2 seconds), so nothing is read on the UI thread here.
        var window = new ConfigWindow(_services, _imports!, _updater!);
        window.Closed += (_, _) => _configWindow = null;
        _configWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Tells the user something: in the open config window's status bar, otherwise as a tray balloon.</summary>
    private void ShowStatus(string message)
    {
        if (_shuttingDown)
            return;
        if (_configWindow is not null)
            _configWindow.ShowStatus(message);
        else
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
        if (_shuttingDown)
            return;

        // An import question is open: the services must not be disposed under it. The user answers first.
        if (_imports?.IsBusy == true)
        {
            _tray?.ShowBalloon(AnswerFirstText);
            return;
        }

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
            {
                // The window stayed open. Either the user said "keep my changes" (nothing to add), or it is busy with an import,
                // an export or a save and cannot be closed right now.
                if (window.IsBusy)
                    _tray?.ShowBalloon(AnswerFirstText);
                return;
            }
        }

        _shuttingDown = true;
        if (_imports is not null)
            _imports.IsQuitting = true; // no new import question from here on
        _services?.RequestShutdown(); // a running cache refresh (no question open) stops cleanly: its RSI requests are cancelled
        _updater?.StopFillMissing(TimeSpan.FromMilliseconds(500)); // give the background fill a moment to stop before the database is disposed
        Shutdown();
    }

    private static void ShowListProblems(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        var text = new StringBuilder();
        if (errors.Count > 0)
        {
            text.AppendLine("IRIS found problems in your list file (Data\\social-overlay.toml):");
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

        MessageBox.Show(text.ToString(), "IRIS", MessageBoxButton.OK,
            errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Write(DateTimeOffset.UtcNow, "Unexpected error: " + e.Exception);
        MessageBox.Show("Something unexpected went wrong:\n\n" + e.Exception.Message + "\n\nIRIS keeps running.",
            "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
