using System;
using System.Collections.Generic;
using System.Threading;
using Iris.Core.Overlay;
using Iris.Core.Social;

namespace Iris.App.Services;

/// <summary>
/// Keeps IRIS's own overlay windows out of its own screen capture. Desktop Duplication sees everything on screen, the overlay included: brackets and panel colors
/// would pass the detector's color mask and the brackets would break its clearance check. <see cref="Apply"/> asks Windows to leave a window out of captures
/// (<c>SetWindowDisplayAffinity</c> with WDA_EXCLUDEFROMCAPTURE, Windows 10 version 2004 and later; this also hides the overlay from screen recorders, OBS, Discord
/// and screenshots). A window for which that failed is remembered here, and <see cref="DxgiFrameSource"/> paints it black in every captured picture: the whole rectangle
/// for the panel and test windows, only the outlines of the brackets for the brackets window (its rectangle also holds nameplates, which must stay readable).
/// Thread safe: the UI thread registers windows and publishes bracket boxes, the detection thread reads them.
/// </summary>
internal static class CaptureExclusion
{
    /// <summary>The first Windows 10 build that knows WDA_EXCLUDEFROMCAPTURE (version 2004). On older builds the call turns the window into a black box in captures.</summary>
    public const int MinimumBuild = 19041;

    /// <summary>A window that could not be excluded: where it is and how to paint it out.</summary>
    internal readonly record struct MaskWindow(NativeMethods.NativeRect Rect, bool BracketsOnly);

    /// <summary>One bracket pair as drawn: the nameplate box in screen physical pixels and the kind (which decides the bracket shape).</summary>
    internal readonly record struct BracketMask(PixelRect ScreenBox, MatchKind Kind);

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, bool> Unexcluded = new(); // hwnd -> is the brackets window; guarded by Gate
    private static bool _oldBuildLogged;                                  // guarded by Gate

    // Replaced as a whole by the UI thread, read by the detection thread: an array that is never changed after it is published.
    private static BracketMask[] _bracketMasks = Array.Empty<BracketMask>();

    /// <summary>
    /// Excludes the window from capture. Returns false (and remembers the window for masking) when Windows refused or the Windows build is too old; the caller logs it.
    /// <paramref name="isBracketsWindow"/>: only the bracket outlines are painted out for it (see <see cref="PublishBrackets"/>). <paramref name="log"/> gets the
    /// "Windows is too old" reason, once.
    /// </summary>
    public static bool Apply(IntPtr hwnd, bool isBracketsWindow = false, Action<string>? log = null)
    {
        bool excluded;
        if (Environment.OSVersion.Version.Build < MinimumBuild)
        {
            // Before Windows 10 version 2004 the flag is not known: Windows would make the window a black box in captures (and report success). Do not call it.
            excluded = false;
            var logIt = false;
            lock (Gate)
            {
                if (!_oldBuildLogged)
                {
                    _oldBuildLogged = true;
                    logIt = true;
                }
            }

            if (logIt)
                log?.Invoke($"Windows build {Environment.OSVersion.Version.Build} is older than {MinimumBuild} (Windows 10 version 2004): excluding windows from capture is not used there, because it would turn them into black boxes");
        }
        else
        {
            excluded = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
        }

        if (!excluded)
        {
            lock (Gate)
                Unexcluded[hwnd] = isBracketsWindow;
        }

        return excluded;
    }

    /// <summary>Forgets a window (it is being closed). A brackets window that was masked also takes its published boxes with it.</summary>
    public static void Remove(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        bool wasBrackets;
        lock (Gate)
        {
            if (!Unexcluded.Remove(hwnd, out wasBrackets))
                return;
        }

        if (wasBrackets)
            Volatile.Write(ref _bracketMasks, Array.Empty<BracketMask>());
    }

    /// <summary>UI thread: hands the capture thread the brackets that the brackets window shows now (an empty array when it shows none). The array must not be changed afterwards.</summary>
    public static void PublishBrackets(BracketMask[] masks) => Volatile.Write(ref _bracketMasks, masks);

    /// <summary>Detection thread: the latest published brackets (never null, never changed by the caller).</summary>
    public static BracketMask[] LatestBrackets() => Volatile.Read(ref _bracketMasks);

    /// <summary>Screen rectangles of the registered windows that are visible now (physical pixels). Usually empty.</summary>
    public static IReadOnlyList<MaskWindow> VisibleUnexcludedWindows()
    {
        KeyValuePair<IntPtr, bool>[] windows;
        lock (Gate)
        {
            if (Unexcluded.Count == 0)
                return Array.Empty<MaskWindow>();
            windows = new KeyValuePair<IntPtr, bool>[Unexcluded.Count];
            ((ICollection<KeyValuePair<IntPtr, bool>>)Unexcluded).CopyTo(windows, 0);
        }

        var result = new List<MaskWindow>(windows.Length);
        foreach (var (hwnd, bracketsOnly) in windows)
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindowRect(hwnd, out var rect))
                result.Add(new MaskWindow(rect, bracketsOnly));
        }

        return result;
    }
}
