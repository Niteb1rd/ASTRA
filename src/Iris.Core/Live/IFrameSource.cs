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
/// Where the detection loop gets its pictures from. IRIS's own implementation (in the Windows app) copies a rectangle of the screen with DXGI Desktop
/// Duplication; the tests give it images. Not thread safe: one thread grabs.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// Copies <paramref name="screenRegion"/> (physical pixels of the virtual desktop, the coordinates of the game window's rectangle) into a reusable
    /// 8 bit BGR image of exactly that size. A region that is not completely on one screen is <see cref="GrabStatus.Unavailable"/>.
    /// </summary>
    GrabResult Grab(Rect screenRegion);

    /// <summary>
    /// Lets go of whatever the source holds while nothing is being captured (GPU objects, the operating system's duplication of the screen).
    /// The source sets itself up again at the next <see cref="Grab"/>. Cheap to call when there is nothing to release.
    /// </summary>
    void Release();
}
