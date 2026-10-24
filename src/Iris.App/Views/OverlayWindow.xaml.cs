using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Iris.App.Services;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Settings;

namespace Iris.App.Views;

/// <summary>
/// The overlay: a transparent, always-on-top window that lies over the game window (or a monitor, for the test overlay).
/// After the window exists it gets the extended styles "layered, transparent, tool window, no activate": mouse clicks go through it to the game,
/// it never takes the keyboard focus and it is not listed in Alt+Tab. It is excluded from screen capture (<see cref="CaptureExclusion"/>), so IRIS's own capture,
/// recorders and screenshots do not show it (or, with the setting "Show overlay in screenshots and recordings", it is visible there and IRIS paints it out of its own
/// captures instead: <see cref="SetVisibleInCapture"/> switches that at once). It is created once and only hidden when not needed.
/// <see cref="Present"/> is cheap to call again and again: nothing is moved or redrawn unless the content, the size or the settings changed.
///
/// The cost to keep in mind: a layered (transparent) window makes Windows composite and update its full surface on every redraw, however little changed in it,
/// so the cost grows with the window's AREA (a window as big as a 2560x1440 or 3840x2160 game window is expensive at 30 to 120 redraws a second). The live overlay
/// therefore uses TWO windows, each only as big as what it draws (<see cref="OverlayWindowFit"/>): a <see cref="OverlayLayer.Brackets"/> window around the brackets (it
/// is the one that is redrawn at monitor refresh, and only when a bracket moved by a whole pixel), and a <see cref="OverlayLayer.Panel"/> window around the
/// panel and the confirmation text (redrawn only when the entries or the settings change). The test overlay uses one window of the whole size (<see cref="OverlayLayer.All"/>).
///
/// Render at monitor refresh: while the brackets window is shown it hooks <see cref="CompositionTarget.Rendering"/> (once per frame WPF composes, i.e. at the refresh rate
/// of the monitor) and asks <see cref="ModelProvider"/> for the model of THAT frame (the brackets moved from their last tracked position along their velocity to now, at most
/// 50 ms ahead). Only when a bracket moved by a whole pixel is that a new model and the window redrawn. The hook is removed when the window hides. A line with the frames, the
/// redraws and the average milliseconds per frame goes to the log once a minute. The panel window never hooks anything.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Room kept around what is drawn when the window is (re)sized: a bracket that moves a little does not move the window.</summary>
    private const double RoomAround = 64.0;

    private readonly OverlayLayer _layer;
    private readonly Action<string>? _warn;   // a line for the log (problems of the setup, the once-a-minute render statistics)
    private IntPtr _hwnd = IntPtr.Zero;
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

    public OverlayWindow()
        : this(OverlayLayer.All)
    {
    }

    /// <param name="layer">What this window draws.</param>
    /// <param name="warn">Receives a line for the log when something about the window's setup did not work (can be called on the UI thread).</param>
    /// <param name="visibleInCapture">The setting "Show overlay in screenshots and recordings" at the time the window is made.</param>
    public OverlayWindow(OverlayLayer layer, Action<string>? warn = null, bool visibleInCapture = false)
    {
        _layer = layer;
        _warn = warn;
        _visibleInCapture = visibleInCapture;
        InitializeComponent();
        Surface.Layer = layer;
    }

    /// <summary>
    /// The brackets layer only: the model for the frame that is being rendered now (the argument is the model this window shows; the same instance comes back when
    /// nothing moved by a whole pixel). Set by the controller; without it the window draws what <see cref="Present"/> gives it and hooks nothing.
    /// </summary>
    public Func<OverlayModel, OverlayModel>? ModelProvider { get; set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle, new IntPtr(style));

        // IRIS's own screen capture must not see this window (brackets and panel colors would reach the detector). Also hides it from recorders and screenshots.
        _excluded = CaptureExclusion.Apply(_hwnd, _layer == OverlayLayer.Brackets, _visibleInCapture, _warn);
        if (!_excluded && !_visibleInCapture)
        {
            _warn?.Invoke(_layer == OverlayLayer.Brackets
                ? "The Brackets overlay window could not be excluded from screen capture (needs Windows 10 version 2004 or later); IRIS paints only the bracket outlines black in its own captures instead"
                : $"The {_layer} overlay window could not be excluded from screen capture (needs Windows 10 version 2004 or later); IRIS paints its rectangle black in its own captures instead");
        }

        ApplyBounds();
        PublishBracketMasks();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopRenderHook();
        CaptureExclusion.Remove(_hwnd);
        base.OnClosed(e);
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
        if (_hwnd == IntPtr.Zero)
            return; // not created yet: OnSourceInitialized uses the flag

        _excluded = CaptureExclusion.Apply(_hwnd, _layer == OverlayLayer.Brackets, _visibleInCapture, _warn);
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
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRenderHook()
    {
        if (!_hooked)
            return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
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

    /// <summary>One monitor frame: the brackets move to where they are now; the window is redrawn only if a bracket moved by a whole pixel (a new model instance).</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        var provider = ModelProvider;
        if (!IsVisible || !_hasBounds || _settings is null || _model is null || provider is null)
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
        if (stats is null)
            return;
        stats.AddFrame((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, redrew);
    }

    /// <summary>
    /// Only when this is the brackets window and it could not be excluded from capture: tells the capture thread where the brackets are, in screen physical pixels,
    /// worked out the way the window draws them (the window sits at the rounded screen position of its fit; the canvas shifts the model by the fit's origin).
    /// </summary>
    private void PublishBracketMasks()
    {
        if (_layer != OverlayLayer.Brackets || _excluded || _hwnd == IntPtr.Zero)
            return;
        if (_model is null || !_hasBounds || _fit.IsEmpty || !IsVisible)
        {
            CaptureExclusion.PublishBrackets(Array.Empty<CaptureExclusion.BracketMask>());
            return;
        }

        var dx = Math.Round(_bounds.X + _fit.X) - _fit.X;
        var dy = Math.Round(_bounds.Y + _fit.Y) - _fit.Y;
        var publishedMs = StopwatchClock.Shared.NowMs;
        var masks = new CaptureExclusion.BracketMask[_model.Brackets.Count];
        for (var i = 0; i < masks.Length; i++)
        {
            var item = _model.Brackets[i];
            masks[i] = new CaptureExclusion.BracketMask(new PixelRect(item.Box.X + dx, item.Box.Y + dy, item.Box.Width, item.Box.Height), item.Kind, _bounds.Height, publishedMs);
        }

        CaptureExclusion.PublishBrackets(masks); // a new array each time; never changed after this
    }

    /// <summary>
    /// Moving the window to another monitor can change its DPI, and WPF then resizes it to what Windows suggests.
    /// The size must stay exactly what was asked for, so it is set again afterwards.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyBounds();
        Surface.InvalidateVisual();
    }

    /// <summary>
    /// Shows <paramref name="model"/> over <paramref name="bounds"/> (screen pixels). The model's coordinates are relative to the top-left
    /// corner of that rectangle. Shows the window if it is hidden (and hides it when this layer has nothing to draw). <paramref name="toast"/> is a short
    /// confirmation text drawn just above the panel's top (panel and "all" layers). The window itself only covers the part of the rectangle that is drawn.
    /// </summary>
    public void Present(OverlayModel model, PixelRect bounds, AppSettings settings, string? toast = null)
    {
        var boundsChanged = !_hasBounds || bounds != _bounds;
        var contentChanged = boundsChanged || !SameContent(model) || settings != _settings || toast != _toast;
        if (!contentChanged && IsVisible)
            return; // nothing moved, nothing to redraw: the hot path of the tracking tick

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
            PublishBracketMasks(); // the window is hidden now, so this publishes "no brackets"
            return;
        }

        var fitChanged = fit != _fit;
        _fit = fit;
        if (boundsChanged || fitChanged)
            ApplyBounds();

        Surface.SetContent(model, layout, toastLayout, toastLayout is null ? null : toast, fit, bounds.Height);

        if (!IsVisible)
        {
            Show();       // the first Show creates the window handle and runs OnSourceInitialized
            ApplyBounds(); // and puts it on top again after a hide
        }

        StartRenderHook(); // brackets layer: from now on every monitor frame asks for the model of that frame
        PublishBracketMasks(); // after Show: the first Show is what runs OnSourceInitialized and decides whether masking is needed
    }

    /// <summary>Hides the window (it is kept for the next time).</summary>
    public void HideOverlay()
    {
        StopRenderHook();
        if (IsVisible)
            Hide();
        PublishBracketMasks();
    }

    /// <summary>The same thing as last time? The brackets layer looks at the model (the live pipeline keeps the instance while nothing moved), the panel layer at the entries.</summary>
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

    /// <summary>Puts the window at its place in real pixels, on top of everything, without activating it. A DIP-based Left/Width would be off on scaled monitors.</summary>
    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero || !_hasBounds || _fit.IsEmpty)
            return;
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HwndTopmost,
            (int)Math.Round(_bounds.X + _fit.X), (int)Math.Round(_bounds.Y + _fit.Y),
            (int)Math.Round(_fit.Width), (int)Math.Round(_fit.Height),
            NativeMethods.SwpNoActivate);
    }
}
