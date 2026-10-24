using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Services;

namespace Iris.App.Views;

/// <summary>
/// Keeps WPF rendering at the monitor's refresh while the brackets are shown. The brackets window used to be a WPF window, and <see cref="CompositionTarget.Rendering"/> (the
/// per-frame callback that drives the brackets) fires once per frame WPF composes: with a visible WPF window on the game's monitor. The brackets window is now a raw Win32 layered
/// window (<see cref="LayeredWindow"/>), so IRIS may have no visible WPF window at all while the game is played (the settings window is closed, the tray icon is not WPF), and
/// whether the event would still fire then is not something IRIS wants to depend on. This is one 1 x 1 pixel, fully transparent, click-through, never-activated WPF window at the
/// top-left corner of the game window, shown while at least one user holds it (the brackets window while it is shown) and hidden a few seconds after the last one lets go. It draws nothing
/// and never moves with the brackets, so it has nothing to do with the one-frame shift that the move of a WPF window caused. UI thread only.
/// </summary>
internal static class RenderKeepAlive
{
    /// <summary>How long the window stays after the last user let go: the brackets window hides and shows again every few seconds in play, and a window that is shown and hidden each time would be churn.</summary>
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(3);

    private static Window? _window;
    private static DispatcherTimer? _hideTimer;
    private static int _users;
    private static int _x;
    private static int _y;

    /// <summary>One more user: the window is shown (at the top-left of <paramref name="x"/>, <paramref name="y"/>, screen pixels) if it was not.</summary>
    public static void Acquire(int x, int y)
    {
        _users++;
        _x = x;
        _y = y;
        _hideTimer?.Stop();
        EnsureWindow();
        if (!_window!.IsVisible)
            _window.Show();
        Place();
    }

    /// <summary>Moves it to another corner (the game window moved); does nothing while nobody holds it.</summary>
    public static void MoveTo(int x, int y)
    {
        if (_users == 0 || (x == _x && y == _y))
            return;
        _x = x;
        _y = y;
        Place();
    }

    /// <summary>One user less: the window is hidden a few seconds after the last one let go (not inside the caller, which may be a rendering callback).</summary>
    public static void Release()
    {
        if (_users == 0)
            return;
        _users--;
        if (_users != 0 || _window is null)
            return;
        _hideTimer ??= CreateHideTimer();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private static DispatcherTimer CreateHideTimer()
    {
        var timer = new DispatcherTimer { Interval = HideAfter };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_users == 0)
                _window?.Hide();
        };
        return timer;
    }

    /// <summary>Closes the window for good (IRIS is quitting).</summary>
    public static void Shutdown()
    {
        _users = 0;
        _hideTimer?.Stop();
        _hideTimer = null;
        var window = _window;
        _window = null;
        try
        {
            window?.Close();
        }
        catch (InvalidOperationException)
        {
            // already closing
        }
    }

    private static void EnsureWindow()
    {
        if (_window is not null)
            return;
        var window = new Window
        {
            Title = "IRIS render clock",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Width = 1,
            Height = 1,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            style |= NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style));
        };
        _window = window;
    }

    /// <summary>One pixel at (x, y) in real pixels (a DIP based Left/Top would be off on scaled monitors).</summary>
    private static void Place()
    {
        var window = _window;
        if (window is null)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, _x, _y, 1, 1, NativeMethods.SwpNoActivate);
    }
}
