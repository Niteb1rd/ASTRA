using System.Diagnostics.CodeAnalysis;
using Iris.Core.Detection;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>
/// What to copy from the screen and how to tell the detector about it. Only the scan region (default x 15 to 85 %, y 10 to 70 % of the game window)
/// is copied, plus the strip of context the detector wants around it (<see cref="NameplateDetectorSettings.ContextPadding"/> to the left and right,
/// <see cref="NameplateDetectorSettings.VerticalContextPadding"/> above and below),
/// so that a line near the region's edge is judged on exactly what it would see in a whole frame. The detector is then given this crop with
/// <see cref="DetectorSettings"/>: the region fractions are re-expressed for the crop (so the detector's own region is exactly the nominal one, to the pixel),
/// and <see cref="NameplateDetectorSettings.ScaleHeight"/> is the height of the game window, so the pixel constants are scaled by the real screen.
/// What the detector returns is in crop pixels; <see cref="CropToWindow"/> turns it into game-window pixels (the overlay model's coordinates).
/// </summary>
public sealed class ScanGeometry
{
    private ScanGeometry(int windowWidth, int windowHeight, Rect capture, NameplateDetectorSettings detectorSettings)
    {
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        Capture = capture;
        DetectorSettings = detectorSettings;
    }

    public int WindowWidth { get; }

    public int WindowHeight { get; }

    /// <summary>The rectangle to copy, in game-window pixels.</summary>
    public Rect Capture { get; }

    /// <summary>The detector settings for the crop (see the class remarks).</summary>
    public NameplateDetectorSettings DetectorSettings { get; }

    public bool Matches(int windowWidth, int windowHeight) => windowWidth == WindowWidth && windowHeight == WindowHeight;

    /// <summary>The capture rectangle in screen pixels for a game window whose top-left corner is at (<paramref name="windowLeft"/>, <paramref name="windowTop"/>).</summary>
    public Rect ToScreen(int windowLeft, int windowTop) => new(windowLeft + Capture.X, windowTop + Capture.Y, Capture.Width, Capture.Height);

    /// <summary>A rectangle in crop pixels (what the detector and the tracker work with) in game-window pixels.</summary>
    public Rect CropToWindow(Rect crop) => new(crop.X + Capture.X, crop.Y + Capture.Y, crop.Width, crop.Height);

    /// <summary>
    /// The geometry for a game window of this size, or false when the window is too small for a scan region (or the settings are not usable).
    /// <paramref name="baseSettings"/> carries the nominal scan region (fractions of the whole window) and the detector numbers.
    /// </summary>
    public static bool TryCreate(int windowWidth, int windowHeight, NameplateDetectorSettings baseSettings, [NotNullWhen(true)] out ScanGeometry? geometry)
    {
        geometry = null;
        if (windowWidth <= 0 || windowHeight <= 0 || baseSettings.Validate() is not null)
            return false;

        // the same arithmetic as NameplateDetector.FindCandidates: int(fraction * size), truncating
        var x0 = (int)(baseSettings.RegionLeft * windowWidth);
        var x1 = Math.Min(windowWidth, (int)(baseSettings.RegionRight * windowWidth));
        var y0 = (int)(baseSettings.RegionTop * windowHeight);
        var y1 = Math.Min(windowHeight, (int)(baseSettings.RegionBottom * windowHeight));
        if (x1 <= x0 || y1 <= y0)
            return false;

        // the detector looks for obstacles this far beside the region, and its colour filter reads a little above and below it
        var pad = baseSettings.ContextPadding(windowHeight);
        var vpad = baseSettings.VerticalContextPadding(windowHeight);
        var cx0 = Math.Max(0, x0 - pad);
        var cx1 = Math.Min(windowWidth, x1 + pad);
        var cy0 = Math.Max(0, y0 - vpad);
        var cy1 = Math.Min(windowHeight, y1 + vpad);
        var capture = new Rect(cx0, cy0, cx1 - cx0, cy1 - cy0);

        // Fractions for the crop that the detector's truncation turns back into exactly x0 - cx0 .. x1 - cx0 and 0 .. height: the extra 0.25 of a pixel keeps
        // floating point noise from truncating to the pixel before.
        var w = (double)capture.Width;
        var h = (double)capture.Height;
        var settings = baseSettings with
        {
            RegionLeft = (x0 - cx0 + 0.25) / w,
            RegionRight = Math.Min(1.0, (x1 - cx0 + 0.25) / w),
            RegionTop = (y0 - cy0 + 0.25) / h,
            RegionBottom = Math.Min(1.0, (y1 - cy0 + 0.25) / h),
            ScaleHeight = windowHeight,
        };
        if (settings.Validate() is not null)
            return false;
        geometry = new ScanGeometry(windowWidth, windowHeight, capture, settings);
        return true;
    }
}
