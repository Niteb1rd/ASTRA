using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Services;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Settings;

namespace Iris.App.Views;

/// <summary>
/// The overlay: a transparent, always-on-top window that lies over the game window (or a monitor, for the test overlay). It is a RAW WIN32 LAYERED WINDOW
/// (<see cref="LayeredWindow"/>), not a WPF window, and each update is ONE call: the visuals are drawn by <see cref="OverlayPainter"/> into a <see cref="DrawingVisual"/>, rendered at
/// 96 dpi into a <see cref="RenderTargetBitmap"/> of exactly the window's size in physical pixels (premultiplied BGRA), copied into a 32 bit top-down DIB section, and
/// <c>UpdateLayeredWindow</c> sets the window's position, size and pixels together. (With a WPF window Windows moved and resized the window on one step and repainted it on another:
/// on a refresh in which the window's rectangle changed but the old picture was still shown, every bracket was shifted by the move distance for one frame, up to 692 px in a
/// 60 fps recording, whenever a bracket appeared or went.)
/// The window has the extended styles "layered, transparent, tool window, no activate, topmost": mouse clicks go through it to the game, it never takes the keyboard focus and it
/// is not listed in Alt+Tab. It is excluded from screen capture (<see cref="CaptureExclusion"/>), so IRIS's own capture, recorders and screenshots do not show it (or, with the
/// setting "Show overlay in screenshots and recordings", it is visible there and IRIS paints it out of its own captures instead: <see cref="SetVisibleInCapture"/> switches that at
/// once). It is created the first time it is shown and only hidden when not needed; <see cref="Close"/> destroys it.
/// <see cref="Present"/> is cheap to call again and again: nothing is redrawn unless the content, the size or the settings changed.
///
/// The cost to keep in mind: a layered window makes Windows composite and update its full surface on every update, however little changed in it, and the bitmap is drawn and
/// copied in full, so the cost grows with the window's AREA (a window as big as a 2560x1440 or 3840x2160 game window is expensive at 30 to 120 updates a second). The live overlay
/// therefore uses TWO windows, each only as big as what it draws (<see cref="OverlayWindowFit"/>): a <see cref="OverlayLayer.Brackets"/> window around the brackets (it
/// is the one that is updated at monitor refresh, and only when a bracket moved by a whole pixel), and a <see cref="OverlayLayer.Panel"/> window around the
/// panel and the confirmation text (updated only when the entries or the settings change). The test overlay uses one window of the whole size (<see cref="OverlayLayer.All"/>).
///
/// Render at monitor refresh: while the brackets window is shown it hooks <see cref="CompositionTarget.Rendering"/> (once per frame WPF composes, i.e. at the refresh rate
/// of the monitor) and asks <see cref="ModelProvider"/> for the model of THAT frame (the brackets moved from their last tracked position along their velocity to now, at most
/// 50 ms ahead). Only when a bracket moved by a whole pixel is that a new model and the window updated. The hook is removed when the window hides; <see cref="RenderKeepAlive"/>
/// makes sure WPF keeps composing while it is hooked. The frames, the redraws and the average milliseconds per frame are counted across hides and written to the log by
/// <see cref="LogRenderSummary"/>, once a minute. The panel window never hooks anything. UI thread only.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    /// <summary>Room kept around what is drawn when the window is (re)sized: a bracket that moves a little does not change the window's size (and its bitmap).</summary>
    private const double RoomAround = 64.0;

    private readonly OverlayLayer _layer;
    private readonly Action<string>? _warn;   // a line for the log (problems of the setup, the once-a-minute render statistics)
    private readonly OverlayPainter _painter;
    private LayeredWindow? _window;           // created the first time it is needed
    private bool _failed;                     // Windows refused to make the window or its bitmap: said once, nothing is drawn after that
    private bool _closed;
    private bool _updateFailedLogged;
    private bool _visibleInCapture;           // the setting "Show overlay in screenshots and recordings" (WDA_NONE, IRIS paints the window out of its own captures)
    private bool _excluded = true;            // false when this window is not excluded from capture by Windows (then the capture thread paints it out)
    private bool _hooked;                     // CompositionTarget.Rendering is hooked (brackets layer, while shown)
    private RenderStats? _stats;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private PixelRect _bounds;                // the game window (or monitor), screen pixels
    private bool _hasBounds;
    private PixelRect _fit;                   // the part of it this window covers, game-window pixels
    private OverlayModel? _model;
    private AppSettings? _settings;
    private string? _toast;

    // what the window shows now: the model, bounds and fit of the last update that Windows accepted (the bracket masks publish THESE, the rectangles actually shown)
    private OverlayModel? _shownModel;
    private PixelRect _shownBounds;
    private PixelRect _shownFit;

    // the drawing surface, reused while the size does not change
    private DrawingVisual? _visual;
    private RenderTargetBitmap? _target;
    private int _targetWidth;
    private int _targetHeight;

    /// <param name="layer">What this window draws.</param>
    /// <param name="warn">Receives a line for the log when something about the window's setup did not work (can be called on the UI thread).</param>
    /// <param name="visibleInCapture">The setting "Show overlay in screenshots and recordings" at the time the window is made.</param>
    public OverlayWindow(OverlayLayer layer, Action<string>? warn = null, bool visibleInCapture = false)
    {
        _layer = layer;
        _warn = warn;
        _visibleInCapture = visibleInCapture;
        _painter = new OverlayPainter { Layer = layer };
    }

    /// <summary>
    /// The brackets layer only: the model for the frame that is being rendered now (the argument is the model this window shows; the same instance comes back when
    /// nothing moved by a whole pixel). Set by the controller; without it the window draws what <see cref="Present"/> gives it and hooks nothing.
    /// </summary>
    public Func<OverlayModel, OverlayModel>? ModelProvider { get; set; }

    private bool IsShown => _window is { IsShown: true };

    private IntPtr Handle => _window?.Handle ?? IntPtr.Zero;

    /// <summary>Destroys the window (the controller calls it when IRIS quits). The object is not usable afterwards.</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        StopRenderHook();
        var window = _window;
        _window = null;
        if (window is not null)
        {
            CaptureExclusion.Remove(window.Handle);
            window.Dispose();
        }

        _visual = null;
        _target = null;
    }

    /// <summary>Makes the window the first time it is needed and gives it its capture treatment. False when it cannot be made (said once in the log).</summary>
    private bool EnsureWindow()
    {
        if (_window is not null)
            return true;
        if (_failed || _closed)
            return false;

        try
        {
            _window = new LayeredWindow();
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            Fail($"The {_layer} overlay window could not be created ({ex.Message})");
            return false;
        }

        // IRIS's own screen capture must not see this window (brackets and panel colors would reach the detector). Also hides it from recorders and screenshots.
        _excluded = CaptureExclusion.Apply(Handle, _layer == OverlayLayer.Brackets, _visibleInCapture, _warn);
        if (!_excluded && !_visibleInCapture)
        {
            _warn?.Invoke(_layer == OverlayLayer.Brackets
                ? "The Brackets overlay window could not be excluded from screen capture (needs Windows 10 version 2004 or later); IRIS paints only the bracket outlines black in its own captures instead"
                : $"The {_layer} overlay window could not be excluded from screen capture (needs Windows 10 version 2004 or later); IRIS paints its rectangle black in its own captures instead");
        }

        return true;
    }

    private void Fail(string text)
    {
        _failed = true;
        _warn?.Invoke(text + "; the overlay stays off");
    }

    /// <summary>
    /// The setting "Show overlay in screenshots and recordings" changed: on = the window is part of screenshots and recordings (WDA_NONE) and IRIS paints it out of its own
    /// captures; off = Windows leaves it out of every capture (WDA_EXCLUDEFROMCAPTURE). Applies at once, also while the window is shown; a window that does not exist yet
    /// is made the right way. Call it on the UI thread.
    /// </summary>
    public void SetVisibleInCapture(bool visible)
    {
        if (visible == _visibleInCapture)
            return;
        _visibleInCapture = visible;
        if (_window is null)
            return; // not created yet: EnsureWindow uses the flag

        _excluded = CaptureExclusion.Apply(Handle, _layer == OverlayLayer.Brackets, _visibleInCapture, _warn);
        if (!_excluded && !_visibleInCapture)
            _warn?.Invoke($"The {_layer} overlay window could not be excluded from screen capture (needs Windows 10 version 2004 or later); IRIS paints it black in its own captures instead");
        PublishBracketMasks();
    }

    // ----- render at monitor refresh -----

    private void StartRenderHook()
    {
        if (_hooked || _layer != OverlayLayer.Brackets || ModelProvider is null)
            return;
        _hooked = true;
        // The statistics live as long as the window, NOT as long as the hook: the brackets window hides and shows again every time the last bracket goes and the next one
        // comes (every few seconds in play), and a line written at every hide was the "Overlay render" line every ~3 s. The line is written by LogRenderSummary, once a minute.
        _stats ??= new RenderStats(StopwatchClock.Shared.NowMs);
        _lastRenderingTime = TimeSpan.MinValue;
        RenderKeepAlive.Acquire((int)Math.Round(_bounds.X), (int)Math.Round(_bounds.Y));
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRenderHook()
    {
        if (!_hooked)
            return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
        RenderKeepAlive.Release();
    }

    /// <summary>
    /// Writes the "Overlay render: N frames in S s, R redraws, average X ms per frame" line for everything since the last call (nothing when no frame was rendered) and starts a
    /// new window. The controller calls it right after the once-a-minute "Live detection" line; nothing else writes it. UI thread.
    /// </summary>
    public void LogRenderSummary()
    {
        var stats = _stats;
        if (stats is null)
            return;
        var line = stats.TakeSummary(StopwatchClock.Shared.NowMs); // also starts the next window when there was nothing to report
        if (stats.LastSummaryFrames > 0)
            _warn?.Invoke(line);
    }

    /// <summary>One monitor frame: the brackets move to where they are now; the window is updated only if a bracket moved by a whole pixel (a new model instance).</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        var provider = ModelProvider;
        if (!IsShown || !_hasBounds || _settings is null || _model is null || provider is null)
            return;
        // WPF can raise the event more than once for one frame: the model of a frame is made once
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime)
                return;
            _lastRenderingTime = args.RenderingTime;
        }

        var started = Stopwatch.GetTimestamp();
        var stats = _stats;
        var model = provider(_model);
        var redrew = !ReferenceEquals(model, _model);
        if (redrew)
            Present(model, _bounds, _settings); // may hide the window (no brackets left), which removes the hook
        stats?.AddFrame((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, redrew);
    }

    /// <summary>
    /// Tells the capture thread where the brackets are, in screen physical pixels, worked out the way the window draws them (the window sits at the rounded screen position of its
    /// fit; the painter shifts the model by the fit's origin). Only for the brackets window when it could not be excluded from capture. It publishes what the window SHOWS (the model
    /// of the last update Windows accepted), nothing while it is hidden.
    /// </summary>
    private void PublishBracketMasks()
    {
        if (_layer != OverlayLayer.Brackets || _excluded || _window is null)
            return;
        if (_shownModel is null || _shownFit.IsEmpty || !IsShown)
        {
            CaptureExclusion.PublishBrackets(Array.Empty<CaptureExclusion.BracketMask>());
            return;
        }

        var dx = Math.Round(_shownBounds.X + _shownFit.X) - _shownFit.X;
        var dy = Math.Round(_shownBounds.Y + _shownFit.Y) - _shownFit.Y;
        var publishedMs = StopwatchClock.Shared.NowMs;
        var masks = new CaptureExclusion.BracketMask[_shownModel.Brackets.Count];
        for (var i = 0; i < masks.Length; i++)
        {
            var item = _shownModel.Brackets[i];
            masks[i] = new CaptureExclusion.BracketMask(new PixelRect(item.Box.X + dx, item.Box.Y + dy, item.Box.Width, item.Box.Height), item.Kind, _shownBounds.Height, publishedMs);
        }

        CaptureExclusion.PublishBrackets(masks); // a new array each time; never changed after this
    }

    /// <summary>
    /// Shows <paramref name="model"/> over <paramref name="bounds"/> (screen pixels). The model's coordinates are relative to the top-left
    /// corner of that rectangle. Shows the window if it is hidden (and hides it when this layer has nothing to draw). <paramref name="toast"/> is a short
    /// confirmation text drawn just above the panel's top (panel and "all" layers). The window itself only covers the part of the rectangle that is drawn.
    /// </summary>
    public void Present(OverlayModel model, PixelRect bounds, AppSettings settings, string? toast = null)
    {
        if (_closed || _failed)
            return;

        var boundsChanged = !_hasBounds || bounds != _bounds;
        var contentChanged = boundsChanged || !SameContent(model) || settings != _settings || toast != _toast;
        if (!contentChanged && IsShown)
            return; // nothing moved, nothing to redraw: the hot path of the tracking

        _bounds = bounds;
        _hasBounds = true;
        _model = model;
        _settings = settings;
        _toast = toast;

        var showsPanel = _layer != OverlayLayer.Brackets;
        var layout = showsPanel ? PanelLayout.Compute(model.Panel, bounds.Width, bounds.Height, settings) : PanelLayoutResult.Empty;
        var toastLayout = showsPanel && toast is not null ? PanelLayout.ComputeToast(bounds.Width, bounds.Height, settings) : null;

        var fit = Fit(model, layout, toastLayout, bounds);
        if (fit.IsEmpty)
        {
            HideOverlay(); // this layer has nothing to draw
            return;
        }

        var fitChanged = fit != _fit;
        _fit = fit;
        if (!EnsureWindow())
            return;

        _painter.SetContent(model, layout, toastLayout, toastLayout is null ? null : toast, fit, bounds.Height);
        if (!Update())
        {
            _model = null; // nothing was shown for this model: the next Present draws again, and the frame hook has nothing to compare with
            return;
        }

        _shownModel = model;
        _shownBounds = bounds;
        _shownFit = fit;

        var window = _window!;
        var firstShow = !window.IsShown;
        if (firstShow)
            window.Show(); // the pixels are already in place, so the first thing on screen is the right picture
        if (firstShow || boundsChanged || fitChanged)
            window.AssertTopmost();
        if (boundsChanged)
            RenderKeepAlive.MoveTo((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));

        StartRenderHook(); // brackets layer: from now on every monitor frame asks for the model of that frame
        PublishBracketMasks(); // after the update: it publishes the rectangles that are shown now
    }

    /// <summary>Hides the window (it is kept for the next time).</summary>
    public void HideOverlay()
    {
        StopRenderHook();
        _window?.Hide();
        _shownModel = null;
        PublishBracketMasks();
    }

    /// <summary>
    /// ONE update of the window: draw the current content into the bitmap of the window's size (physical pixels, 96 dpi), copy its premultiplied BGRA into the DIB section and call
    /// <c>UpdateLayeredWindow</c>, which sets position, size and pixels together. False when it did not work.
    /// </summary>
    private bool Update()
    {
        var window = _window!;
        var width = (int)Math.Round(_fit.Width);
        var height = (int)Math.Round(_fit.Height);
        if (width <= 0 || height <= 0)
            return false;

        var bits = window.BeginSurface(width, height);
        if (bits == IntPtr.Zero)
        {
            Fail($"The {_layer} overlay window's bitmap could not be made ({width}x{height})");
            return false;
        }

        try
        {
            if (_visual is null || _target is null || _targetWidth != width || _targetHeight != height)
            {
                _visual ??= new DrawingVisual();
                _target = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
                _targetWidth = width;
                _targetHeight = height;
            }

            _target.Clear(); // a render adds to what is there
            using (var context = _visual.RenderOpen())
                _painter.Paint(context);
            _target.Render(_visual);
            _target.CopyPixels(new Int32Rect(0, 0, width, height), bits, window.Stride * height, window.Stride);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException or COMException)
        {
            if (!_updateFailedLogged)
            {
                _updateFailedLogged = true;
                _warn?.Invoke($"The {_layer} overlay could not be drawn ({ex.GetType().Name}: {ex.Message}); it is tried again at the next change");
            }

            _target = null; // a new bitmap next time
            return false;
        }

        var x = (int)Math.Round(_bounds.X + _fit.X);
        var y = (int)Math.Round(_bounds.Y + _fit.Y);
        if (window.Commit(x, y))
            return true;
        if (!_updateFailedLogged)
        {
            _updateFailedLogged = true;
            _warn?.Invoke($"Windows refused to update the {_layer} overlay window (UpdateLayeredWindow failed); it is tried again at the next change");
        }

        return false;
    }

    /// <summary>The same thing as last time? The brackets layer looks at the model (the live pipeline keeps the instance while nothing moves), the panel layer at the entries.</summary>
    private bool SameContent(OverlayModel model) =>
        _model is not null && (_layer == OverlayLayer.Panel ? ReferenceEquals(model.Panel, _model.Panel) : ReferenceEquals(model, _model));

    /// <summary>
    /// The part of the game window (game-window pixels) this window covers: all of it for the test overlay, otherwise the extent of what the layer draws, with room
    /// to spare and kept as it is while it still fits (<see cref="OverlayWindowFit.Choose"/>). Empty when there is nothing to draw.
    /// </summary>
    private PixelRect Fit(OverlayModel model, PanelLayoutResult layout, ToastLayout? toastLayout, PixelRect bounds)
    {
        var whole = new PixelRect(0, 0, bounds.Width, bounds.Height);
        if (_layer == OverlayLayer.All)
            return model.IsEmpty && toastLayout is null ? default : whole;

        var parts = new List<PixelRect>();
        if (_layer == OverlayLayer.Brackets)
        {
            foreach (var item in model.Brackets)
                parts.Add(BracketMetrics.ExtentOf(item.Box, bounds.Height));
        }
        else
        {
            foreach (var slot in layout.Slots)
                parts.Add(slot.Rect);
            if (layout.MoreText is not null)
                parts.Add(layout.MoreRect);
            if (toastLayout is not null)
                parts.Add(toastLayout.Rect);
        }

        var needed = OverlayWindowFit.Union(parts);
        if (needed is null)
            return default;
        // the panel does not move: it gets just a little air (for the antialiased edges); the brackets get room to move in
        var margin = _layer == OverlayLayer.Panel ? 4.0 : RoomAround;
        return OverlayWindowFit.Choose(_fit.IsEmpty ? null : _fit, needed.Value, whole, margin);
    }
}
