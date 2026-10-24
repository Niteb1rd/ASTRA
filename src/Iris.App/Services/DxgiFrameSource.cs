using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Social;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

/// <summary>What <see cref="DxgiFrameSource.AcquireNextFrame"/> found.</summary>
public enum FrameWait
{
    /// <summary>The screen has a new picture; the frame is held until it is copied or released.</summary>
    NewFrame,

    /// <summary>Nothing new on screen: the wait timed out, or only the mouse pointer changed. Nothing is held.</summary>
    Unchanged,

    /// <summary>Nothing can be captured now (see <see cref="FrameArrival.Message"/>); the source sets itself up again by itself.</summary>
    Unavailable,
}

/// <summary>
/// The answer of <see cref="DxgiFrameSource.AcquireNextFrame"/>. <paramref name="FrameMs"/> is the time the picture was presented, on the live clock (NaN unless a new frame),
/// <paramref name="AccumulatedFrames"/> how many updates of the screen were merged into it (1 normally, more when frames were not picked up in time).
/// </summary>
public readonly record struct FrameArrival(FrameWait Status, double FrameMs = double.NaN, int AccumulatedFrames = 0, string? Message = null);

/// <summary>
/// Captures rectangles of the screen with DXGI Desktop Duplication (Windows 8 and later; IRIS targets Windows 11). The picture stays on the GPU until the
/// wanted rectangles (the scan region, a fraction of the screen, and the small search patches of the tracking) are copied into small CPU-readable staging textures and
/// from there into reusable BGR <see cref="Mat"/>s; the rest of the screen is never read back.
/// <list type="bullet">
/// <item>The output (monitor) is the one that holds the regions (<see cref="NativeMethods.TryGetMonitorForRect"/>, matched with the output's monitor handle); the Direct3D 11
/// device is created on that output's adapter, as duplication requires.</item>
/// <item>Event driven, in three steps the capture loop calls itself: <see cref="AcquireNextFrame"/> (<c>AcquireNextFrame</c>: waits up to <see cref="AcquireTimeoutMs"/> for the screen
/// to get a new picture, reports its present time and holds the frame), then EITHER <see cref="CopyHeldFrame"/> (one <c>CopySubresourceRegion</c> for EACH region out of that one frame,
/// <c>ReleaseFrame</c> at once, then for each region: map, convert) OR <see cref="ReleaseHeldFrame"/> (the loop decided not to track this frame). The frame is never held longer than that.
/// Every region has its own slot (its own staging texture, whose size is rounded up to <see cref="StagingBucket"/> pixels so that a patch that changes by a few pixels does not make a new
/// texture, and its own BGR Mat). A wait that times out (nothing changed on screen) is <see cref="FrameWait.Unchanged"/>, answered by <see cref="UnchangedRegions"/> as
/// <see cref="GrabStatus.NoNewFrame"/>: a region that is still where it was keeps its previous picture, any other has none. <see cref="GrabRegions(IReadOnlyList{Rect},bool)"/> is the three steps in one call.</item>
/// <item>Anything else that goes wrong (access lost: a mode change, another full-screen program, the secure desktop; device removed or reset; the monitor of the game window
/// changed; an unexpected pixel format) releases everything (device, duplication, staging textures) and the next grab sets up again, no sooner than a short pause after a
/// failed setup (<see cref="RetryAfterFailureMs"/>).</item>
/// <item>Every returned picture has IRIS's own overlay windows painted black where Windows does not leave them out of the capture itself (<see cref="CaptureExclusion"/>):
/// the panel rectangle and the bracket outlines of the last 3 published sets.</item>
/// </list>
/// <c>DuplicateOutput</c> always delivers 8 bit B8G8R8A8, also while HDR is on for the monitor (Windows then converts the picture; the 16 bit float formats need
/// <c>DuplicateOutput1</c>, which IRIS does not use). So HDR does not stop the capture, but the converted colors can lower the detection quality. R8G8B8A8 is accepted too,
/// although <c>DuplicateOutput</c> is not known to produce it; any other format is reported as unsupported. Not thread safe: ONE thread (the capture loop) calls everything.
/// The Vortice calls are the only place IRIS touches Direct3D or DXGI.
/// </summary>
public sealed class DxgiFrameSource : IFrameSource
{
    /// <summary>
    /// How long <see cref="AcquireNextFrame"/> waits for the screen to get a new picture before it reports "unchanged": long enough to be idle on a still screen, short enough
    /// that the caller looks at its stop and gate flags often. A constant on purpose: it is passed straight to Vortice's call, whose parameter is a uint or an int
    /// depending on the version, and a constant converts to either.
    /// </summary>
    public const int AcquireTimeoutMs = 50;

    /// <summary>After a setup that failed, the next try is made no sooner than this (milliseconds).</summary>
    public const long RetryAfterFailureMs = 2000;

    /// <summary>The staging textures are as wide and high as the region rounded UP to a multiple of this (pixels).</summary>
    public const int StagingBucket = 64;

    /// <summary>One region of a call: its staging texture, its picture, and what the picture is of.</summary>
    private sealed class Slot
    {
        public Vd.ID3D11Texture2D? Staging;
        public int StagingWidth;   // bucketed
        public int StagingHeight;
        public Vx.Format Format;
        public Mat? Bgr;           // exactly Region's size
        public Rect Region;        // where Bgr was taken (screen pixels)
        public bool HasPicture;    // Bgr holds a picture of the current setup and of Region
    }

    private readonly List<Slot> _slots = new();   // the pool of the search patches: a patch keeps its slot (same picture, else same size bucket)
    private readonly Slot _scan = new();          // the scan region has its own slot: it never shares a texture with a patch
    private Vd.ID3D11Device? _device;
    private Vd.ID3D11DeviceContext? _context;
    private Vx.IDXGIOutputDuplication? _duplication;
    private Vx.IDXGIResource? _held;              // the acquired frame that has not been copied or released yet (at most one)
    private double _heldMs;                       // its time on the live clock
    private IntPtr _monitor;
    private PixelRect _outputBounds;
    private long _retryAtTick;
    private bool _disposed;

    /// <inheritdoc />
    public GrabResult Grab(Rect screenRegion)
    {
        var result = GrabRegions(new[] { screenRegion });
        var image = result.Images.Count > 0 ? result.Images[0] : null;
        if (result.Status == GrabStatus.NoNewFrame && image is null)
            return new GrabResult(GrabStatus.NoNewFrame, null, result.Message ?? "waiting for the first screen picture");
        return new GrabResult(result.Status, image, result.Message);
    }

    /// <inheritdoc />
    public GrabRegionsResult GrabRegions(IReadOnlyList<Rect> screenRegions) => GrabRegions(screenRegions, firstIsScan: false);

    /// <summary>
    /// <see cref="GrabRegions(IReadOnlyList{Rect})"/> with the first region marked as the detection scan region: it gets a dedicated staging texture and picture, separate
    /// from the pool of the patches, so that no slot ever switches between the scan size and a patch size.
    /// </summary>
    public GrabRegionsResult GrabRegions(IReadOnlyList<Rect> screenRegions, bool firstIsScan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (screenRegions.Count == 0)
            return new GrabRegionsResult(GrabStatus.Ok, Array.Empty<Mat?>());

        // the one-call way: wait for a frame, copy, release (the event-driven loop of the app uses the three calls itself)
        var arrival = AcquireCore(ToPixelRect(screenRegions[0]), "the scan region");
        switch (arrival.Status)
        {
            case FrameWait.NewFrame:
                return CopyHeldFrame(screenRegions, firstIsScan);
            case FrameWait.Unchanged:
                return UnchangedRegions(screenRegions, firstIsScan);
            default:
                return GrabRegionsResult.Unavailable(arrival.Message ?? "screen capture is not available");
        }
    }

    /// <summary>
    /// Waits up to <see cref="AcquireTimeoutMs"/> for the screen of the game window to get a NEW picture (Desktop Duplication wakes the call when the desktop is
    /// updated) and, when it did, HOLDS that frame: the caller then calls <see cref="CopyHeldFrame"/> (which also releases it) or <see cref="ReleaseHeldFrame"/>, and holds it no
    /// longer than it needs (the next acquire gives up a frame that was left). <see cref="FrameWait.Unchanged"/> is a wait that timed out, or a wake for a mouse pointer
    /// update only (no new content: <c>AccumulatedFrames</c> and <c>LastPresentTime</c> both 0): nothing is held then. <see cref="FrameArrival.FrameMs"/> is the time the picture
    /// was presented, on the live clock (<see cref="FrameClock"/>: the frame's <c>LastPresentTime</c> in QPC ticks, or the clock right after the acquire when that is 0 or implausible).
    /// <paramref name="window"/> (physical screen pixels) says which monitor to duplicate.
    /// </summary>
    public FrameArrival AcquireNextFrame(Rect window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AcquireCore(ToPixelRect(window), "the game window");
    }

    /// <summary>Gives up the frame that <see cref="AcquireNextFrame"/> is holding, without copying anything. Does nothing when none is held.</summary>
    public void ReleaseHeldFrame()
    {
        var resource = _held;
        _held = null;
        if (resource is null)
            return;
        ReleaseFrameQuietly();
        resource.Dispose();
    }

    /// <summary>
    /// The answer for a still screen: <see cref="GrabStatus.NoNewFrame"/>, where a region that is where it was keeps its previous picture of this setup and any other has none
    /// (the detection loop reads the previous scan picture again, so that the tracks keep being seen).
    /// </summary>
    public GrabRegionsResult UnchangedRegions(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var assigned = AssignSlots(regions, firstIsScan);
        var previous = new List<Mat?>(regions.Count);
        for (var i = 0; i < regions.Count; i++)
        {
            var slot = assigned[i];
            var keep = slot is { HasPicture: true, Bgr: { IsDisposed: false } bgr } && slot.Region == regions[i] && !bgr.Empty();
            previous.Add(keep ? slot.Bgr : null);
        }

        return new GrabRegionsResult(
            GrabStatus.NoNewFrame, previous, previous.Exists(m => m is not null) ? null : "waiting for the first screen picture", StopwatchClock.Shared.NowMs);
    }

    /// <summary>
    /// Copies <paramref name="regions"/> (physical screen pixels) out of the frame <see cref="AcquireNextFrame"/> holds and releases it. The result's
    /// <see cref="GrabRegionsResult.FrameMs"/> is that frame's time. Nothing held, or a region that is not completely on the duplicated screen, is
    /// <see cref="GrabStatus.Unavailable"/> (and the frame is released).
    /// </summary>
    public GrabRegionsResult CopyHeldFrame(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = _held;
        if (resource is null || _duplication is null)
        {
            ReleaseHeldFrame();
            return GrabRegionsResult.Unavailable("no screen frame was held");
        }

        var frameMs = _heldMs;
        if (regions.Count == 0)
        {
            ReleaseHeldFrame();
            return new GrabRegionsResult(GrabStatus.Ok, Array.Empty<Mat?>(), null, frameMs);
        }

        var bounds = _outputBounds;
        foreach (var region in regions)
        {
            var rect = ToPixelRect(region);
            if (rect.X < bounds.X || rect.Y < bounds.Y || rect.Right > bounds.Right || rect.Bottom > bounds.Bottom)
            {
                ReleaseHeldFrame();
                return GrabRegionsResult.Unavailable("a capture region is not completely on one screen");
            }
        }

        try
        {
            return CopyRegions(resource, frameMs, regions, bounds, firstIsScan); // releases the frame
        }
        catch (SharpGenException ex)
        {
            ReleaseHeldFrame();
            Release();
            _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
            return GrabRegionsResult.Unavailable("screen capture failed and will be set up again: " + ex.Message);
        }
    }

    /// <summary>Finds (and when needed sets up) the duplication of the monitor that holds <paramref name="probe"/>, then waits for and acquires the next frame.</summary>
    private FrameArrival AcquireCore(PixelRect probe, string what)
    {
        ReleaseHeldFrame(); // a frame that an earlier call left unreleased
        if (!NativeMethods.TryGetMonitorForRect(probe, out var monitor, out var bounds))
            return new FrameArrival(FrameWait.Unavailable, Message: what + " is not on any screen");

        // the game window moved to another screen, or the screen's mode changed: set up again
        if (_duplication is not null && (monitor != _monitor || bounds != _outputBounds))
            Release();

        try
        {
            if (_duplication is null)
            {
                if (Environment.TickCount64 < _retryAtTick)
                    return new FrameArrival(FrameWait.Unavailable, Message: "screen capture is waiting to retry after a failed setup");
                var problem = SetUp(monitor, bounds);
                if (problem is not null)
                {
                    Release();
                    _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
                    return new FrameArrival(FrameWait.Unavailable, Message: problem);
                }
            }

            return Acquire();
        }
        catch (SharpGenException ex)
        {
            // for example DXGI_ERROR_ACCESS_LOST or DEVICE_REMOVED thrown by a call that checks its result, or DuplicateOutput refused
            Release();
            _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
            return new FrameArrival(FrameWait.Unavailable, Message: "screen capture failed and will be set up again: " + ex.Message);
        }
    }

    /// <inheritdoc />
    public void Release()
    {
        ReleaseHeldFrame();
        foreach (var slot in AllSlots())
        {
            slot.Staging?.Dispose();
            slot.Staging = null;
            slot.StagingWidth = 0;
            slot.StagingHeight = 0;
            slot.HasPicture = false;
        }

        _duplication?.Dispose(); // disposing the duplication also gives up a frame that may still be held
        _duplication = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _monitor = IntPtr.Zero;
        _outputBounds = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Release();
        foreach (var slot in AllSlots())
        {
            slot.Bgr?.Dispose();
            slot.Bgr = null;
        }

        _slots.Clear();
        _disposed = true;
    }

    private IEnumerable<Slot> AllSlots()
    {
        yield return _scan;
        foreach (var slot in _slots)
            yield return slot;
    }

    /// <summary>
    /// A slot for each region. The scan region (when <paramref name="firstIsScan"/>) always gets <see cref="_scan"/>. A patch gets the slot that holds the picture of the very same
    /// rectangle (for the "unchanged screen" answer), else a free slot with a staging texture of its size bucket, else any free slot, else a new one: so a patch keeps its texture
    /// from tick to tick whatever happens to the other patches.
    /// </summary>
    private Slot[] AssignSlots(IReadOnlyList<Rect> regions, bool firstIsScan)
    {
        var result = new Slot?[regions.Count];
        var used = new HashSet<Slot>();
        var start = 0;
        if (firstIsScan && regions.Count > 0)
        {
            result[0] = _scan;
            used.Add(_scan);
            start = 1;
        }

        for (var i = start; i < regions.Count; i++)
            result[i] = _slots.FirstOrDefault(slot => !used.Contains(slot) && slot.HasPicture && slot.Region == regions[i]);
        for (var i = start; i < regions.Count; i++)
        {
            if (result[i] is null)
            {
                var width = Bucket(regions[i].Width);
                var height = Bucket(regions[i].Height);
                result[i] = _slots.FirstOrDefault(slot => !used.Contains(slot) && slot.Staging is not null && slot.StagingWidth == width && slot.StagingHeight == height);
            }

            if (result[i] is not null)
                used.Add(result[i]!);
        }

        for (var i = start; i < regions.Count; i++)
        {
            if (result[i] is not null)
                continue;
            var slot = _slots.FirstOrDefault(s => !used.Contains(s));
            if (slot is null)
            {
                slot = new Slot();
                _slots.Add(slot);
            }

            result[i] = slot;
            used.Add(slot);
        }

        return result!;
    }

    private static PixelRect ToPixelRect(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Finds the output of <paramref name="monitor"/>, creates the device on its adapter and duplicates the output. Returns null, or why it failed.</summary>
    private string? SetUp(IntPtr monitor, PixelRect bounds)
    {
        if (Vx.DXGI.CreateDXGIFactory1<Vx.IDXGIFactory1>(out var factory).Failure || factory is null)
            return "DXGI could not be started";
        using (factory)
        {
            // The indexes are uint in Vortice 3.x. If the build says "cannot convert uint to int" (an older Vortice), use int for a and o instead.
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            if (output.Description.Monitor != monitor)
                                continue;
                            return Duplicate(adapter, output, monitor, bounds);
                        }
                    }
                }
            }
        }

        return "DXGI has no output for the monitor that shows the game";
    }

    private string? Duplicate(Vx.IDXGIAdapter1 adapter, Vx.IDXGIOutput output, IntPtr monitor, PixelRect bounds)
    {
        var levels = new[] { Vc.FeatureLevel.Level_11_1, Vc.FeatureLevel.Level_11_0, Vc.FeatureLevel.Level_10_1, Vc.FeatureLevel.Level_10_0 };
        if (Vd.D3D11.D3D11CreateDevice(adapter, Vc.DriverType.Unknown, Vd.DeviceCreationFlags.BgraSupport, levels, out Vd.ID3D11Device? device).Failure || device is null)
            return "a Direct3D 11 device could not be created on the graphics adapter of the game's monitor";
        _device = device;
        _context = device.ImmediateContext;

        using var output1 = output.QueryInterface<Vx.IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(device); // throws a SharpGenException when refused (access denied, not available, unsupported)
        _monitor = monitor;
        _outputBounds = bounds;
        return null;
    }

    /// <summary>
    /// <c>AcquireNextFrame</c> on the duplication. A frame with new content is HELD (<see cref="_held"/>, <see cref="_heldMs"/>); a timeout, or a wake with no new content
    /// (a mouse pointer update only), is "unchanged"; a failure releases everything and the next acquire sets up again.
    /// </summary>
    private FrameArrival Acquire()
    {
        var duplication = _duplication!;
        // Depending on the Vortice version AcquireNextFrame returns the Result or throws a SharpGenException for a failed one (a timeout included): both are handled here.
        Result acquired;
        Vx.IDXGIResource? resource = null;
        long presentTicks = 0;
        long accumulated = 0;
        try
        {
            // AcquireTimeoutMs is a constant: it converts to the uint (or int) parameter of this Vortice version
            acquired = duplication.AcquireNextFrame(AcquireTimeoutMs, out var info, out resource);
            presentTicks = info.LastPresentTime; // QueryPerformanceCounter ticks of the last present, 0 when only the mouse pointer changed
            accumulated = info.AccumulatedFrames; // how many updates were merged into this frame
        }
        catch (SharpGenException ex)
        {
            acquired = ex.ResultCode;
            resource = null;
        }

        if (acquired == Vx.ResultCode.WaitTimeout)
        {
            resource?.Dispose();
            return new FrameArrival(FrameWait.Unchanged); // nothing changed on screen
        }

        if (acquired.Failure || resource is null)
        {
            resource?.Dispose();
            // access lost (mode change, secure desktop, another full-screen program) and everything else: start from scratch next time
            var lost = acquired == Vx.ResultCode.AccessLost;
            Release();
            _retryAtTick = Environment.TickCount64 + (lost ? 0 : RetryAfterFailureMs);
            return new FrameArrival(FrameWait.Unavailable, Message: lost ? "screen capture was lost and is being set up again" : "screen capture failed: " + acquired);
        }

        if (accumulated == 0 && presentTicks == 0)
        {
            // only the mouse pointer moved or changed shape: no new picture
            _held = resource;
            ReleaseHeldFrame();
            return new FrameArrival(FrameWait.Unchanged);
        }

        // the time of the picture: when it was presented (the clock reading right after the acquire when the stamp is 0 or not plausible); never earlier than the previous frame's
        var acquiredAtMs = StopwatchClock.Shared.NowMs;
        var frameMs = FrameClock.FrameMs(presentTicks, StopwatchClock.Shared.OriginTimestamp, Stopwatch.Frequency, acquiredAtMs);
        _heldMs = frameMs;
        _held = resource;
        return new FrameArrival(FrameWait.NewFrame, frameMs, (int)Math.Min(accumulated, int.MaxValue));
    }

    /// <summary>
    /// Copies the regions out of the held frame <paramref name="resource"/> (taken at <paramref name="frameMs"/>), releases it, then reads the copies back.
    /// </summary>
    private GrabRegionsResult CopyRegions(Vx.IDXGIResource resource, double frameMs, IReadOnlyList<Rect> regions, PixelRect outputBounds, bool firstIsScan)
    {
        // what IRIS's own windows looked like at the time of the picture: taken now, before the copies (which take time), so that the masks are the ones of the picture and
        // not of a later redraw
        var windows = CaptureExclusion.VisibleUnexcludedWindows();
        var brackets = windows.Count == 0 ? Array.Empty<CaptureExclusion.BracketMask[]>() : CaptureExclusion.RecentBrackets(frameMs);
        var slots = AssignSlots(regions, firstIsScan);

        try
        {
            using var frame = resource.QueryInterface<Vd.ID3D11Texture2D>();
            var description = frame.Description;
            if (description.Width != (uint)outputBounds.Width || description.Height != (uint)outputBounds.Height)
            {
                // start from scratch at the next grab (no sooner than the retry pause, so this cannot loop tightly)
                ReleaseHeldFrame();
                Release();
                _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
                return GrabRegionsResult.Unavailable($"the screen picture is {description.Width}x{description.Height}, not the {(int)outputBounds.Width}x{(int)outputBounds.Height} of the monitor (a rotated screen is not supported)");
            }

            if (description.Format != Vx.Format.B8G8R8A8_UNorm && description.Format != Vx.Format.R8G8B8A8_UNorm)
            {
                ReleaseHeldFrame();
                Release();
                _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
                return GrabRegionsResult.Unavailable($"the screen picture has pixel format {description.Format}, which IRIS cannot read (only 8 bit B8G8R8A8 and R8G8B8A8 are supported)");
            }

            // every region is copied out of the ONE frame that is held now
            for (var i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var slot = slots[i];
                EnsureStaging(slot, region.Width, region.Height, description.Format);
                var left = region.X - (int)outputBounds.X;
                var top = region.Y - (int)outputBounds.Y;
                // Vortice 3.x keeps Box in Vortice.Mathematics (int fields), not in Vortice.Direct3D11.
                var box = new Vortice.Mathematics.Box(left, top, 0, left + region.Width, top + region.Height, 1);
                _context!.CopySubresourceRegion(slot.Staging!, 0, 0, 0, 0, frame, 0, box);
            }
        }
        finally
        {
            ReleaseHeldFrame(); // the frame is never kept across calls (releases it on the duplication and disposes the resource)
        }

        var images = new List<Mat?>(regions.Count);
        for (var i = 0; i < regions.Count; i++)
        {
            var slot = slots[i];
            ReadStaging(slot, regions[i]);
            MaskOwnWindows(slot.Bgr!, regions[i], windows, brackets);
            images.Add(slot.Bgr);
        }

        return new GrabRegionsResult(GrabStatus.Ok, images, null, frameMs);
    }

    private static int Bucket(int size) => (size + StagingBucket - 1) / StagingBucket * StagingBucket;

    private void EnsureStaging(Slot slot, int width, int height, Vx.Format format)
    {
        var bucketWidth = Bucket(width);
        var bucketHeight = Bucket(height);
        if (slot.Staging is not null && slot.StagingWidth == bucketWidth && slot.StagingHeight == bucketHeight && slot.Format == format)
            return;
        slot.Staging?.Dispose();
        slot.Staging = null;
        slot.HasPicture = false; // the Mat belongs to the old texture
        var description = new Vd.Texture2DDescription
        {
            Width = (uint)bucketWidth, // uint in Vortice 3.x (int in older versions: then drop the casts)
            Height = (uint)bucketHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new Vx.SampleDescription(1, 0),
            Usage = Vd.ResourceUsage.Staging,
            BindFlags = Vd.BindFlags.None,
            CPUAccessFlags = Vd.CpuAccessFlags.Read,
            MiscFlags = Vd.ResourceOptionFlags.None,
        };
        slot.Staging = _device!.CreateTexture2D(description);
        slot.StagingWidth = bucketWidth;
        slot.StagingHeight = bucketHeight;
        slot.Format = format;
    }

    /// <summary>Maps the slot's staging texture, converts the region's part of it (BGRA or RGBA) to BGR into the slot's reusable Mat and unmaps.</summary>
    private void ReadStaging(Slot slot, Rect region)
    {
        var staging = slot.Staging!;
        var context = _context!;
        var mapped = context.Map(staging, 0, Vd.MapMode.Read, Vd.MapFlags.None);
        try
        {
            slot.Bgr ??= new Mat();
            using var source = Mat.FromPixelData(region.Height, region.Width, MatType.CV_8UC4, mapped.DataPointer, mapped.RowPitch);
            var code = slot.Format == Vx.Format.B8G8R8A8_UNorm ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.RGBA2BGR;
            Cv2.CvtColor(source, slot.Bgr, code);
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        slot.Region = region;
        slot.HasPicture = true;
    }

    /// <summary>
    /// Second layer against IRIS capturing its own overlay: windows that could not be excluded from capture (<see cref="CaptureExclusion"/>) are painted black in
    /// the picture, so that brackets and panel colors never reach the detector. Nothing to do (the usual case) when every overlay window is excluded by Windows.
    /// </summary>
    private static void MaskOwnWindows(Mat bgr, Rect region, IReadOnlyList<CaptureExclusion.MaskWindow> windows, IReadOnlyList<CaptureExclusion.BracketMask[]> brackets)
    {
        if (bgr.Empty() || windows.Count == 0)
            return;
        var bracketsMasked = false;
        foreach (var mask in windows)
        {
            if (mask.BracketsOnly)
            {
                // The brackets window's rectangle also holds nameplates: painting all of it black would hide them (and make the brackets flicker in the picture).
                // Only the outlines of the brackets are painted out.
                if (!bracketsMasked)
                {
                    MaskBracketOutlines(bgr, region, brackets);
                    bracketsMasked = true;
                }

                continue;
            }

            var window = mask.Rect;
            var left = Math.Max(window.Left, region.X) - region.X;
            var top = Math.Max(window.Top, region.Y) - region.Y;
            var right = Math.Min(window.Right, region.X + region.Width) - region.X;
            var bottom = Math.Min(window.Bottom, region.Y + region.Height) - region.Y;
            if (right <= left || bottom <= top)
                continue;
            Cv2.Rectangle(bgr, new Rect(left, top, right - left, bottom - top), new Scalar(0, 0, 0), -1);
        }
    }

    /// <summary>Extra thickness (pixels) of the black outline over the drawn brackets, so that the antialiased edges and rounding never leave a colored rim.</summary>
    private const int BracketMaskExtra = 4;

    /// <summary>
    /// Paints the bracket shapes black, each as a line as thick as the widest thing drawn for it (the dark outline: colored stroke plus the outline on both sides)
    /// plus <see cref="BracketMaskExtra"/>. The boxes are those of the last <see cref="CaptureExclusion.HistoryLength"/> models the brackets window drew, in screen pixels as the
    /// window draws them; the Mat is the capture region, so drawing outside it is clipped by OpenCV.
    /// </summary>
    private static void MaskBracketOutlines(Mat bgr, Rect region, IReadOnlyList<CaptureExclusion.BracketMask[]> brackets)
    {
        var black = new Scalar(0, 0, 0);
        foreach (var set in brackets) // the last published sets, newest first: the screen may still show an earlier frame than the newest
        foreach (var item in set)
        {
            var box = item.ScreenBox;
            var m = BracketMetrics.For(box, item.GameHeight);
            var extent = m.Extent(box);
            if (extent.Right < region.X || extent.X > region.X + region.Width || extent.Bottom < region.Y || extent.Y > region.Y + region.Height)
                continue; // nothing of it in the picture
            var thickness = (int)Math.Ceiling(m.Stroke + 2 * m.Outline) + BracketMaskExtra;
            Cv2.Polylines(bgr, new[] { BracketPoints(item.Kind, m.LeftX(box), m.Top, m.Width, m.Height, false, region) }, false, black, thickness, LineTypes.Link8);
            Cv2.Polylines(bgr, new[] { BracketPoints(item.Kind, m.RightX(box), m.Top, m.Width, m.Height, true, region) }, false, black, thickness, LineTypes.Link8);
        }
    }

    /// <summary>
    /// The points (picture pixels, relative to <paramref name="region"/>) of one bracket as an open line: the same shape the overlay draws (OverlayCanvas.BracketShape;
    /// keep the two in step), with the curves cut into short straight pieces.
    /// </summary>
    private static Point[] BracketPoints(MatchKind kind, double x, double y, double width, double height, bool mirrored, Rect region)
    {
        var points = new List<Point>(24);
        (double U, double V) current = (1, 0);

        void Add(double u, double v) =>
            points.Add(new Point(
                (int)Math.Round((mirrored ? x + (1 - u) * width : x + u * width) - region.X),
                (int)Math.Round(y + v * height - region.Y)));

        void Line(double u, double v)
        {
            Add(u, v);
            current = (u, v);
        }

        void Curve(double cu, double cv, double u, double v)
        {
            const int steps = 6;
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                var s = 1 - t;
                Add(s * s * current.U + 2 * s * t * cu + t * t * u, s * s * current.V + 2 * s * t * cv + t * t * v);
            }

            current = (u, v);
        }

        Add(1, 0);
        switch (kind)
        {
            case MatchKind.PlayerList: // curly: end, spine, tip in the middle, spine, end
                Curve(0.5, 0, 0.5, 0.12);
                Line(0.5, 0.38);
                Curve(0.5, 0.5, 0, 0.5);
                Curve(0.5, 0.5, 0.5, 0.62);
                Line(0.5, 0.88);
                Curve(0.5, 1, 1, 1);
                break;

            case MatchKind.OrgList: // square
                Line(0, 0);
                Line(0, 1);
                Line(1, 1);
                break;

            default: // angle: OrgAttribute and NoMatch
                Line(0, 0.5);
                Line(1, 1);
                break;
        }

        return points.ToArray();
    }

    private void ReleaseFrameQuietly()
    {
        try
        {
            _duplication?.ReleaseFrame();
        }
        catch (SharpGenException)
        {
            // there was no frame to release (or the duplication was lost): nothing to do
        }
    }
}
