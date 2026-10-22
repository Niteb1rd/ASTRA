using System;
using System.Runtime.InteropServices;
using Iris.Core.Overlay;

namespace Iris.App.Services;

/// <summary>
/// The few Windows calls the overlay needs (window styles, window and monitor rectangles, the foreground window).
/// All rectangles are in physical pixels: the app is per-monitor DPI aware (see app.manifest).
/// The "Ptr" functions exist under that name on 64-bit Windows only, which is what IRIS runs on (Windows 11).
/// </summary>
internal static class NativeMethods
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;

    public const long WsExTransparent = 0x00000020L;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExLayered = 0x00080000L;
    public const long WsExNoActivate = 0x08000000L;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    public static readonly IntPtr HwndTopmost = new IntPtr(-1);

    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public const uint ModNoRepeat = 0x4000;
    public const int WmHotkey = 0x0312;
    public static readonly IntPtr HwndMessage = new IntPtr(-3);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out NativeRect value, int size);

    /// <summary>The window style flags (WS_...) of a window.</summary>
    public static uint GetStyle(IntPtr hwnd) => unchecked((uint)GetWindowLongPtr(hwnd, GwlStyle).ToInt64());

    /// <summary>
    /// The visible rectangle of a window in screen pixels: the DWM "extended frame bounds" (without the invisible resize border),
    /// or GetWindowRect when DWM has none. False when neither gives a usable rectangle.
    /// </summary>
    public static bool TryGetWindowBounds(IntPtr hwnd, out PixelRect bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) == 0 && IsUsable(frame))
        {
            bounds = ToPixelRect(frame);
            return true;
        }
        if (GetWindowRect(hwnd, out var rect) && IsUsable(rect))
        {
            bounds = ToPixelRect(rect);
            return true;
        }
        bounds = default;
        return false;
    }

    /// <summary>
    /// The full rectangle of the monitor a window is on (the nearest one if it is between monitors; the primary monitor when
    /// <paramref name="hwnd"/> is zero). False when Windows gives no answer.
    /// </summary>
    public static bool TryGetMonitorBounds(IntPtr hwnd, out PixelRect bounds)
    {
        var monitor = MonitorFromWindow(hwnd, hwnd == IntPtr.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && IsUsable(info.Monitor))
            {
                bounds = ToPixelRect(info.Monitor);
                return true;
            }
        }
        bounds = default;
        return false;
    }

    private static bool IsUsable(NativeRect r) => r.Right > r.Left && r.Bottom > r.Top;

    private static PixelRect ToPixelRect(NativeRect r) => PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
}
