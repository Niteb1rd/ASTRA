using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Iris.App.Services;

/// <summary>
/// A raw Win32 per-pixel-alpha layered window, updated with ONE call: <c>UpdateLayeredWindow</c> sets the window's position, size and pixels together, so a window that
/// moves and changes picture at the same time is never shown with the new place and the old picture (a WPF window is moved and resized by Windows on one step and repainted on
/// another, which showed every bracket shifted by the move distance for one frame whenever the window's rectangle changed).
/// <list type="bullet">
/// <item>No WPF, no <c>HwndSource</c>: <c>RegisterClassExW</c> once per process (the window procedure is a static delegate that is kept alive for the life of the process) and
/// <c>CreateWindowExW</c> with the styles WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST and WS_POPUP. The window never takes the focus (WM_MOUSEACTIVATE answers
/// MA_NOACTIVATE, shown with SW_SHOWNOACTIVATE) and mouse clicks go through it (WS_EX_TRANSPARENT, and WM_NCHITTEST answers HTTRANSPARENT). It is created hidden.</item>
/// <item>The pixels live in a 32 bit top-down DIB section (BGRA, premultiplied alpha: what the layered window's AC_SRC_ALPHA blend wants) selected into a memory DC. The DIB is
/// kept while the size does not change (<see cref="BeginSurface"/>).</item>
/// <item>Not thread safe: create it, use it and dispose it on the UI thread (the thread that pumps its messages).</item>
/// </list>
/// </summary>
internal sealed class LayeredWindow : IDisposable
{
    private const string ClassName = "IrisOverlayLayeredWindow";

    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;

    private const uint WmMouseActivate = 0x0021;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly IntPtr MaNoActivate = new(3);

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private const uint DibRgbColors = 0;
    private const int ErrorClassAlreadyExists = 1410;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    // The window procedure must outlive every window of the class: a static field, never collected (a delegate that is only referenced from native code would be).
    private static readonly WndProcDelegate WndProcInstance = WndProc;
    private static bool _classRegistered;

    private IntPtr _hwnd;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;       // the DIB section selected into _memoryDc
    private IntPtr _stockBitmap;  // what the memory DC had selected before ours, put back before the DC is deleted
    private IntPtr _bits;
    private int _width;
    private int _height;
    private bool _shown;
    private bool _disposed;

    /// <summary>Creates the window (hidden). Throws <see cref="Win32Exception"/> when Windows refuses.</summary>
    public LayeredWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        RegisterClassOnce(instance);

        var exStyle = unchecked((uint)(NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)) | WsExTopmost;
        _hwnd = CreateWindowExW(exStyle, ClassName, "IRIS overlay", WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            throw new Win32Exception(error);
        }
    }

    /// <summary>The window handle (for the display affinity, the capture exclusion and the z-order).</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>True between <see cref="Show"/> and <see cref="Hide"/>.</summary>
    public bool IsShown => _shown;

    /// <summary>The row length of <see cref="BeginSurface"/>'s pixels in bytes (4 bytes a pixel, no padding): of the whole surface, which may be larger than what <see cref="Commit"/> shows.</summary>
    public int Stride => _width * 4;

    /// <summary>
    /// Makes the DIB (the surface) <paramref name="width"/> x <paramref name="height"/> pixels (kept as it is when the size did not change; the caller passes a bucketed size so that it rarely does) and returns where its pixels are: premultiplied BGRA,
    /// top row first, <see cref="Stride"/> bytes a row. The caller fills ALL of it, then calls <see cref="Commit"/>. The pointer is valid until the next call with another size or the dispose.
    /// Returns <see cref="IntPtr.Zero"/> when Windows could not make the bitmap.
    /// </summary>
    public IntPtr BeginSurface(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
            return IntPtr.Zero;
        if (_bitmap != IntPtr.Zero && width == _width && height == _height)
            return _bits;

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,          // negative: top-down, the first row of the pixels is the top row of the window
                Planes = 1,
                BitCount = 32,
                Compression = 0,           // BI_RGB
                SizeImage = (uint)(width * height * 4),
            },
        };
        var bitmap = CreateDIBSection(_memoryDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap); // a bitmap without pixels is no use and must not leak
            return IntPtr.Zero;
        }

        var previous = SelectObject(_memoryDc, bitmap);
        if (_bitmap == IntPtr.Zero)
            _stockBitmap = previous;         // the first selection returns the DC's own 1x1 bitmap
        else
            DeleteObject(_bitmap);           // the old DIB was the one just replaced
        _bitmap = bitmap;
        _bits = bits;
        _width = width;
        _height = height;
        return _bits;
    }

    /// <summary>
    /// ONE <c>UpdateLayeredWindow</c>: the window is put at (<paramref name="x"/>, <paramref name="y"/>) in screen pixels, sized <paramref name="width"/> x <paramref name="height"/>
    /// (at most the surface; the top-left part of the DIB, source point (0, 0)) and given its pixels, all in the same step. Returns false when Windows refused (the window then stays as it was).
    /// </summary>
    public bool Commit(int x, int y, int width, int height)
    {
        if (_disposed || _hwnd == IntPtr.Zero || _bitmap == IntPtr.Zero || width <= 0 || height <= 0 || width > _width || height > _height)
            return false;

        var position = new NativePoint { X = x, Y = y };
        var size = new NativeSize { Cx = width, Cy = height };
        var source = new NativePoint { X = 0, Y = 0 };
        var blend = new BlendFunction { BlendOp = AcSrcOver, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            return UpdateLayeredWindow(_hwnd, screen, ref position, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (screen != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Shows the window without activating it (it keeps what <see cref="Commit"/> gave it).</summary>
    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        ShowWindow(_hwnd, SwShowNoActivate);
        _shown = true;
    }

    /// <summary>Hides the window (it is kept for the next time).</summary>
    public void Hide()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        ShowWindow(_hwnd, SwHide);
        _shown = false;
    }

    /// <summary>Puts the window on top of the other topmost windows again, without moving, sizing or activating it.</summary>
    public void AssertTopmost()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    /// <summary>Destroys the window and frees the bitmap and the memory DC. Safe to call again.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shown = false;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd); // on the thread that created it, as Windows requires
            _hwnd = IntPtr.Zero;
        }

        if (_memoryDc != IntPtr.Zero)
        {
            if (_stockBitmap != IntPtr.Zero)
                SelectObject(_memoryDc, _stockBitmap); // our DIB must not be selected into a DC when it is deleted
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_bitmap != IntPtr.Zero)
        {
            DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
    }

    // ------------------------------------------------------------------ window class

    private static void RegisterClassOnce(IntPtr instance)
    {
        if (_classRegistered)
            return;
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = 0,
            WndProc = Marshal.GetFunctionPointerForDelegate(WndProcInstance),
            ClassExtra = 0,
            WindowExtra = 0,
            Instance = instance,
            Icon = IntPtr.Zero,
            Cursor = IntPtr.Zero,
            Background = IntPtr.Zero,
            MenuName = null,
            ClassName = ClassName,
            IconSmall = IntPtr.Zero,
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
                throw new Win32Exception(error);
        }

        _classRegistered = true;
    }

    /// <summary>Everything goes to <c>DefWindowProcW</c> except: clicks pass through (HTTRANSPARENT), the window is never activated by the mouse, and the background is not erased.</summary>
    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmNcHitTest:
                    return HtTransparent;
                case WmMouseActivate:
                    return MaNoActivate;
                case WmEraseBkgnd:
                    return new IntPtr(1);
            }
        }
        catch (Exception)
        {
            // an exception must never cross into native code: fall through to the default
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    /// <summary>BLENDFUNCTION: four bytes (BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    /// <summary>BITMAPINFOHEADER, 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    /// <summary>BITMAPINFO: the header and the (here unused) first color table entry.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    /// <summary>WNDCLASSEXW (80 bytes on 64 bit Windows).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public IntPtr IconSmall;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr destinationDc, ref NativePoint destinationPosition, ref NativeSize size, IntPtr sourceDc, ref NativePoint sourcePosition,
        uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
