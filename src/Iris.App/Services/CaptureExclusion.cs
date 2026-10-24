using System;
using System.Collections.Generic;

namespace Iris.App.Services;

/// <summary>
/// Keeps IRIS's own overlay windows out of its own screen capture. Desktop Duplication sees everything on screen, the overlay included: brackets and panel colors
/// would pass the detector's color mask and the brackets would break its clearance check. <see cref="Apply"/> asks Windows to leave a window out of captures
/// (<c>SetWindowDisplayAffinity</c> with WDA_EXCLUDEFROMCAPTURE, Windows 10 version 2004 and later; this also hides the overlay from screen recorders, OBS, Discord
/// and screenshots). A window for which that failed is remembered here, and <see cref="DxgiFrameSource"/> paints its rectangle black in every captured picture.
/// Thread safe: the UI thread registers windows, the detection thread reads them.
/// </summary>
internal static class CaptureExclusion
{
    private static readonly object Gate = new();
    private static readonly List<IntPtr> Unexcluded = new();

    /// <summary>Excludes the window from capture. Returns false (and remembers the window for masking) when Windows refused; the caller logs it.</summary>
    public static bool Apply(IntPtr hwnd)
    {
        var excluded = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
        if (!excluded)
        {
            lock (Gate)
            {
                if (!Unexcluded.Contains(hwnd))
                    Unexcluded.Add(hwnd);
            }
        }

        return excluded;
    }

    /// <summary>Screen rectangles of the registered windows that are visible now (physical pixels). Usually empty.</summary>
    public static IReadOnlyList<NativeMethods.NativeRect> VisibleUnexcludedWindows()
    {
        IntPtr[] windows;
        lock (Gate)
        {
            if (Unexcluded.Count == 0)
                return Array.Empty<NativeMethods.NativeRect>();
            windows = Unexcluded.ToArray();
        }

        var rectangles = new List<NativeMethods.NativeRect>(windows.Length);
        foreach (var hwnd in windows)
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindowRect(hwnd, out var rect))
                rectangles.Add(rect);
        }

        return rectangles;
    }
}
