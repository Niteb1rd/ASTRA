using System;
using System.Runtime.InteropServices;
using System.Text;
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

    private const uint MonitorDefaultToNull = 0;
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

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public const int WhKeyboardLl = 13;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    public const uint WmQuit = 0x0012;
    public const uint PmNoRemove = 0x0000;

    /// <summary>A message from a thread's queue (MSG). The point is two plain ints.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    /// <summary>Waits for the next message of the calling thread's queue. Returns 0 for WM_QUIT, -1 for an error, otherwise a positive number.</summary>
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax);

    /// <summary>Looks at the calling thread's queue without waiting. Calling it once also makes sure the thread has a queue.</summary>
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    public const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

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

    /// <summary>
    /// The monitor that holds most of <paramref name="rect"/> (screen pixels): its handle (the same value DXGI reports as the output's monitor) and its
    /// full rectangle. False when the rectangle is on no monitor or Windows gives no answer.
    /// </summary>
    public static bool TryGetMonitorForRect(PixelRect rect, out IntPtr monitor, out PixelRect bounds)
    {
        var native = new NativeRect
        {
            Left = (int)Math.Round(rect.X),
            Top = (int)Math.Round(rect.Y),
            Right = (int)Math.Round(rect.Right),
            Bottom = (int)Math.Round(rect.Bottom),
        };
        monitor = MonitorFromRect(ref native, MonitorDefaultToNull);
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

    // The system timer normally ticks every 15.6 ms; Thread.Sleep and waits are only as precise as that. The tracking tick (up to 120 a second) asks for 1 ms
    // while detection runs, and gives it back afterwards. Calls must be paired (the system counts them).
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>
    /// The full path of the exe a process runs, asked with the limited-information right (PROCESS_QUERY_LIMITED_INFORMATION), which Windows also
    /// grants for protected (anti-cheat) processes where Process.MainModule would be refused. False when the process cannot be opened or has ended.
    /// </summary>
    public static bool TryGetProcessImagePath(uint processId, out string path)
    {
        path = string.Empty;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            foreach (var capacity in new[] { 1024u, 8192u, 32768u })
            {
                var buffer = new StringBuilder((int)capacity);
                var size = capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    path = buffer.ToString();
                    return path.Length > 0;
                }
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return false;
            }
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsUsable(NativeRect r) => r.Right > r.Left && r.Bottom > r.Top;

    private static PixelRect ToPixelRect(NativeRect r) => PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
}
