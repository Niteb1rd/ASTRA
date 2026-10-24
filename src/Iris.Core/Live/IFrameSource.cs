using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>What <see cref="IFrameSource.Grab"/> got.</summary>
public enum GrabStatus
{
    /// <summary>A new picture was copied into <see cref="GrabResult.Image"/>.</summary>
    Ok,

    /// <summary>
    /// The screen did not change since the last grab (the operating system had no new frame within the wait time). <see cref="GrabResult.Image"/> is still the
    /// previous picture, if there was one: the detection loop reads it again, so that the tracks keep being seen and do not expire on a still screen.
    /// </summary>
    NoNewFrame,

    /// <summary>
    /// Nothing could be captured now (no screen at that place, the capture was lost and is being set up again, an unsupported pixel format).
    /// <see cref="GrabResult.Message"/> says why in plain words. The loop skips this read and asks again at the next one.
    /// </summary>
    Unavailable,
}

/// <summary>
/// The result of one grab. <see cref="Image"/> belongs to the frame source: it stays valid until the next <see cref="IFrameSource.Grab"/>,
/// <see cref="IFrameSource.Release"/> or <see cref="IDisposable.Dispose"/>, and the caller must not dispose it or keep it.
/// </summary>
public readonly record struct GrabResult(GrabStatus Status, Mat? Image, string? Message = null)
{
    public static GrabResult Unavailable(string message) => new(GrabStatus.Unavailable, null, message);
}

/// <summary>
/// The result of one <see cref="IFrameSource.GrabRegions"/>: one image per requested region, in the order of the request, all copied out of ONE acquired
/// screen frame. The images belong to the frame source, as <see cref="GrabResult.Image"/> does (valid until the next grab, release or dispose; the caller
/// must not dispose or keep them). An entry is null when there is no picture for that region: with <see cref="GrabStatus.NoNewFrame"/> that is a region
/// whose place changed since the last grab (the screen did not change, so the previous picture of that place is not there; "unchanged" is the answer),
/// with <see cref="GrabStatus.Unavailable"/> it is every entry. <paramref name="FrameMs"/> is the time of the frame on the live clock
/// (<see cref="StopwatchClock.Shared"/>), taken right after the screen frame was acquired; NaN when the source has no such time (then the caller uses its own clock).
/// </summary>
public readonly record struct GrabRegionsResult(GrabStatus Status, IReadOnlyList<Mat?> Images, string? Message = null, double FrameMs = double.NaN)
{
    public static GrabRegionsResult Unavailable(string message) => new(GrabStatus.Unavailable, Array.Empty<Mat?>(), message);
}

/// <summary>
/// Where the detection loop gets its pictures from. IRIS's own implementation (in the Windows app) copies rectangles of the screen with DXGI Desktop
/// Duplication; the tests give it images. Not thread safe: one thread grabs (the capture loop is the only owner of the screen duplication).
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// Copies <paramref name="screenRegion"/> (physical pixels of the virtual desktop, the coordinates of the game window's rectangle) into a reusable
    /// 8 bit BGR image of exactly that size. A region that is not completely on one screen is <see cref="GrabStatus.Unavailable"/>.
    /// </summary>
    GrabResult Grab(Rect screenRegion);

    /// <summary>
    /// Copies several rectangles (<paramref name="screenRegions"/>, the same coordinates as <see cref="Grab"/>) out of ONE acquired screen frame: the capture
    /// loop reads the small search patches of the tracking and, now and then, the detection scan region from the very same picture, at the cost of one
    /// frame acquisition. Every returned image is a reusable 8 bit BGR image of exactly its region's size. Regions that are not completely on one screen
    /// make the whole call <see cref="GrabStatus.Unavailable"/>.
    /// </summary>
    GrabRegionsResult GrabRegions(IReadOnlyList<Rect> screenRegions);

    /// <summary>
    /// Lets go of whatever the source holds while nothing is being captured (GPU objects, the operating system's duplication of the screen).
    /// The source sets itself up again at the next <see cref="Grab"/>. Cheap to call when there is nothing to release.
    /// </summary>
    void Release();
}
