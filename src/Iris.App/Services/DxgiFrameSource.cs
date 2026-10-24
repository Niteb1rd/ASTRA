using System;
using Iris.Core.Live;
using Iris.Core.Overlay;
using OpenCvSharp;
using SharpGen.Runtime;
using Vc = Vortice.Direct3D;
using Vd = Vortice.Direct3D11;
using Vx = Vortice.DXGI;

namespace Iris.App.Services;

/// <summary>
/// Captures a rectangle of the screen with DXGI Desktop Duplication (Windows 8 and later; IRIS targets Windows 11). The picture stays on the GPU until the
/// wanted rectangle (only the scan region, a fraction of the screen) is copied into a small CPU-readable staging texture and from there into a reusable
/// BGR <see cref="Mat"/>; the rest of the screen is never read back.
/// <list type="bullet">
/// <item>The output (monitor) is the one that holds the region (<see cref="NativeMethods.TryGetMonitorForRect"/>, matched with the output's monitor handle); the Direct3D 11
/// device is created on that output's adapter, as duplication requires.</item>
/// <item>One call = <c>AcquireNextFrame</c> (wait up to <see cref="AcquireTimeoutMs"/>), <c>CopySubresourceRegion</c> of the region, <c>ReleaseFrame</c> at once (the frame is
/// never held across reads), map, convert. A wait that times out (nothing changed on screen) is <see cref="GrabStatus.NoNewFrame"/>: the previous picture is still in the Mat.</item>
/// <item>Anything else that goes wrong (access lost: a mode change, another full-screen program, the secure desktop; device removed or reset; the monitor of the game window
/// changed; an unexpected pixel format) releases everything (device, duplication, staging texture) and the next grab sets up again, no sooner than a short pause after a
/// failed setup (<see cref="RetryAfterFailureMs"/>). The staging texture is made again when the region's size changes.</item>
/// </list>
/// Only 8 bit formats are supported: B8G8R8A8 and R8G8B8A8, which is what Windows gives for a normal (SDR) desktop. Not thread safe: one thread (the detection loop) calls everything.
/// The Vortice calls are the only place IRIS touches Direct3D or DXGI.
/// </summary>
public sealed class DxgiFrameSource : IFrameSource
{
    /// <summary>How long a grab waits for the screen to change before it reports "no new frame".</summary>
    public const int AcquireTimeoutMs = 100;

    /// <summary>After a setup that failed, the next try is made no sooner than this (milliseconds).</summary>
    public const long RetryAfterFailureMs = 2000;

    private Vd.ID3D11Device? _device;
    private Vd.ID3D11DeviceContext? _context;
    private Vx.IDXGIOutputDuplication? _duplication;
    private Vd.ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private Vx.Format _stagingFormat;
    private IntPtr _monitor;
    private PixelRect _outputBounds;
    private Mat? _bgr;
    private long _retryAtTick;
    private bool _disposed;

    /// <inheritdoc />
    public GrabResult Grab(Rect screenRegion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = new PixelRect(screenRegion.X, screenRegion.Y, screenRegion.Width, screenRegion.Height);
        if (!NativeMethods.TryGetMonitorForRect(region, out var monitor, out var bounds))
            return GrabResult.Unavailable("the scan region is not on any screen");
        if (region.X < bounds.X || region.Y < bounds.Y || region.Right > bounds.Right || region.Bottom > bounds.Bottom)
            return GrabResult.Unavailable("the scan region is not completely on one screen");

        // the game window moved to another screen, or the screen's mode changed: set up again
        if (_duplication is not null && (monitor != _monitor || bounds != _outputBounds))
            Release();

        try
        {
            if (_duplication is null)
            {
                if (Environment.TickCount64 < _retryAtTick)
                    return GrabResult.Unavailable("screen capture is waiting to retry after a failed setup");
                var problem = SetUp(monitor, bounds);
                if (problem is not null)
                {
                    Release();
                    _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
                    return GrabResult.Unavailable(problem);
                }
            }

            return CopyRegion(screenRegion, bounds);
        }
        catch (SharpGenException ex)
        {
            // for example DXGI_ERROR_ACCESS_LOST or DEVICE_REMOVED thrown by a call that checks its result, or DuplicateOutput refused
            Release();
            _retryAtTick = Environment.TickCount64 + RetryAfterFailureMs;
            return GrabResult.Unavailable("screen capture failed and will be set up again: " + ex.Message);
        }
    }

    /// <inheritdoc />
    public void Release()
    {
        _staging?.Dispose();
        _staging = null;
        _stagingWidth = 0;
        _stagingHeight = 0;
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
        _bgr?.Dispose();
        _bgr = null;
        _disposed = true;
    }

    /// <summary>Finds the output of <paramref name="monitor"/>, creates the device on its adapter and duplicates the output. Returns null, or why it failed.</summary>
    private string? SetUp(IntPtr monitor, PixelRect bounds)
    {
        if (Vx.DXGI.CreateDXGIFactory1<Vx.IDXGIFactory1>(out var factory).Failure || factory is null)
            return "DXGI could not be started";
        using (factory)
        {
            for (var a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (var o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
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
        if (Vd.D3D11.D3D11CreateDevice(adapter, Vc.DriverType.Unknown, Vd.DeviceCreationFlags.BgraSupport, levels, out Vd.ID3D11Device device).Failure || device is null)
            return "a Direct3D 11 device could not be created on the graphics adapter of the game's monitor";
        _device = device;
        _context = device.ImmediateContext;

        using var output1 = output.QueryInterface<Vx.IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(device); // throws a SharpGenException when refused (access denied, not available, unsupported)
        _monitor = monitor;
        _outputBounds = bounds;
        return null;
    }

    private GrabResult CopyRegion(Rect region, PixelRect outputBounds)
    {
        var duplication = _duplication!;
        var acquired = duplication.AcquireNextFrame(AcquireTimeoutMs, out _, out var resource);
        if (acquired == Vx.ResultCode.WaitTimeout)
            return new GrabResult(GrabStatus.NoNewFrame, _bgr is { IsDisposed: false } && !_bgr.Empty() ? _bgr : null);
        if (acquired.Failure || resource is null)
        {
            // access lost (mode change, secure desktop, another full-screen program) and everything else: start from scratch next time
            var lost = acquired == Vx.ResultCode.AccessLost;
            Release();
            _retryAtTick = Environment.TickCount64 + (lost ? 0 : RetryAfterFailureMs);
            return GrabResult.Unavailable(lost ? "screen capture was lost and is being set up again" : "screen capture failed: " + acquired);
        }

        try
        {
            using var frame = resource.QueryInterface<Vd.ID3D11Texture2D>();
            var description = frame.Description;
            if (description.Width != (int)outputBounds.Width || description.Height != (int)outputBounds.Height)
                return GrabResult.Unavailable($"the screen picture is {description.Width}x{description.Height}, not the {(int)outputBounds.Width}x{(int)outputBounds.Height} of the monitor (a rotated screen is not supported)");
            if (description.Format != Vx.Format.B8G8R8A8_UNorm && description.Format != Vx.Format.R8G8B8A8_UNorm)
                return GrabResult.Unavailable($"the screen picture has pixel format {description.Format}, which IRIS cannot read (HDR is not supported: switch HDR off for the game's monitor)");

            EnsureStaging(region.Width, region.Height, description.Format);
            var left = region.X - (int)outputBounds.X;
            var top = region.Y - (int)outputBounds.Y;
            var box = new Vd.Box(left, top, 0, left + region.Width, top + region.Height, 1);
            _context!.CopySubresourceRegion(_staging!, 0, 0, 0, 0, frame, 0, box);
        }
        finally
        {
            ReleaseFrameQuietly(); // the frame is never kept across reads
            resource.Dispose();
        }

        return ReadStaging(region.Width, region.Height);
    }

    private void EnsureStaging(int width, int height, Vx.Format format)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height && _stagingFormat == format)
            return;
        _staging?.Dispose();
        _staging = null;
        var description = new Vd.Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new Vx.SampleDescription(1, 0),
            Usage = Vd.ResourceUsage.Staging,
            BindFlags = Vd.BindFlags.None,
            CPUAccessFlags = Vd.CpuAccessFlags.Read,
            MiscFlags = Vd.ResourceOptionFlags.None,
        };
        _staging = _device!.CreateTexture2D(description);
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
    }

    /// <summary>Maps the staging texture, converts BGRA (or RGBA) to BGR into the reusable Mat and unmaps.</summary>
    private GrabResult ReadStaging(int width, int height)
    {
        var staging = _staging!;
        var context = _context!;
        var mapped = context.Map(staging, 0, Vd.MapMode.Read, Vd.MapFlags.None);
        try
        {
            _bgr ??= new Mat();
            using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, mapped.DataPointer, mapped.RowPitch);
            var code = _stagingFormat == Vx.Format.B8G8R8A8_UNorm ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.RGBA2BGR;
            Cv2.CvtColor(source, _bgr, code);
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        return new GrabResult(GrabStatus.Ok, _bgr);
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
