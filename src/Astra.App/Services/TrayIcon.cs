using System;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Astra.App.Services;

/// <summary>
/// The system-tray icon and its menu. The only place in the app that uses WinForms (NotifyIcon has no WPF equivalent).
/// It raises plain events; App decides what they do. Dispose it on exit so no ghost icon stays in the tray.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string DefaultTooltip = "ASTRA";
    private const int MaxTooltipLength = 63; // NotifyIcon.Text throws for longer text
    private const int MaxBalloonLength = 200; // keeps the balloon readable (Windows cuts long text anyway)

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private bool _disposed;

    public TrayIcon()
    {
        var settingsItem = new Forms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _updateItem = new Forms.ToolStripMenuItem("Update database now");
        _updateItem.Click += (_, _) => UpdateDatabaseRequested?.Invoke(this, EventArgs.Empty);

        _pauseItem = new Forms.ToolStripMenuItem("Pause overlay (not active yet)") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => PauseChanged?.Invoke(this, _pauseItem.Checked);

        var openLogItem = new Forms.ToolStripMenuItem("Open log");
        openLogItem.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);

        var openConfigItem = new Forms.ToolStripMenuItem("Open config location");
        openConfigItem.Click += (_, _) => OpenConfigLocationRequested?.Invoke(this, EventArgs.Empty);

        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(openLogItem);
        _menu.Items.Add(openConfigItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(quitItem);

        _icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application, // placeholder until ASTRA has its own icon
            Text = DefaultTooltip,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? UpdateDatabaseRequested;

    public event EventHandler? OpenLogRequested;

    public event EventHandler? OpenConfigLocationRequested;

    public event EventHandler? QuitRequested;

    /// <summary>Raised with the new state when the user ticks or unticks "Pause overlay".</summary>
    public event EventHandler<bool>? PauseChanged;

    /// <summary>Greys out "Update database now" while an update runs, and says so in the tooltip.</summary>
    public void SetUpdateRunning(bool running)
    {
        if (_disposed)
            return;
        _updateItem.Enabled = !running;
        SetTooltip(running ? "ASTRA - updating the database..." : DefaultTooltip);
    }

    /// <summary>Shows a balloon notification next to the tray icon.</summary>
    public void ShowBalloon(string text)
    {
        if (_disposed)
            return;
        var shown = text.Length <= MaxBalloonLength ? text : text.Substring(0, MaxBalloonLength - 1) + "…";
        _icon.ShowBalloonTip(8000, "ASTRA", shown, Forms.ToolTipIcon.Info);
    }

    private void SetTooltip(string text)
    {
        _icon.Text = text.Length <= MaxTooltipLength ? text : text.Substring(0, MaxTooltipLength);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false; // removes the icon from the tray right away
        _icon.Dispose();
        _menu.Dispose();
    }
}
