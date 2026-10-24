using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using Iris.App.Services;
using Iris.Core.Overlay;
using Iris.Core.Settings;

namespace Iris.App.Views;

/// <summary>
/// The overlay: a transparent, always-on-top window that lies over the game window (or a monitor, for the test overlay).
/// After the window exists it gets the extended styles "layered, transparent, tool window, no activate": mouse clicks go through it to the game,
/// it never takes the keyboard focus and it is not listed in Alt+Tab. It is excluded from screen capture (<see cref="CaptureExclusion"/>), so IRIS's own capture,
/// recorders and screenshots do not show it. It is created once and only hidden when not needed.
/// <see cref="Present"/> is cheap to call again and again: nothing is moved or redrawn unless the content, the size or the settings changed.
///
/// The cost to keep in mind: a layered (transparent) window makes Windows composite and update its full surface on every redraw, however little changed in it,
/// so the cost grows with the window's AREA (a window as big as a 2560x1440 or 3840x2160 game window is expensive at 30 to 120 redraws a second). The live overlay
/// therefore uses TWO windows, each only as big as what it draws (<see cref="OverlayWindowFit"/>): a <see cref="OverlayLayer.Brackets"/> window around the brackets (it
/// is the one that is redrawn at the tracking tickrate, and only when a bracket moved by a whole pixel), and a <see cref="OverlayLayer.Panel"/> window around the
/// panel and the confirmation text (redrawn only when the entries or the settings change). The test overlay uses one window of the whole size (<see cref="OverlayLayer.All"/>).
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>Room kept around what is drawn when the window is (re)sized: a bracket that moves a little does not move the window.</summary>
    private const double Margin = 64.0;

    private readonly OverlayLayer _layer;
    private readonly Action<string>? _warn;
    private IntPtr _hwnd = IntPtr.Zero;
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
    public OverlayWindow(OverlayLayer layer, Action<string>? warn = null)
    {
        _layer = layer;
        _warn = warn;
        InitializeComponent();
        Surface.Layer = layer;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle, new IntPtr(style));

        // IRIS's own screen capture must not see this window (brackets and panel colors would reach the detector). Also hides it from recorders and screenshots.
        if (!CaptureExclusion.Apply(_hwnd))
            _warn?.Invoke($"The {_layer} overlay window could not be excluded from screen capture (needs Windows 10 version 2004 or later); IRIS paints its rectangle black in its own captures instead");

        ApplyBounds();
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
            return;
        }

        var fitChanged = fit != _fit;
        _fit = fit;
        if (boundsChanged || fitChanged)
            ApplyBounds();

        Surface.SetContent(model, layout, toastLayout, toastLayout is null ? null : toast, fit);

        if (!IsVisible)
        {
            Show();       // the first Show creates the window handle and runs OnSourceInitialized
            ApplyBounds(); // and puts it on top again after a hide
        }
    }

    /// <summary>Hides the window (it is kept for the next time).</summary>
    public void HideOverlay()
    {
        if (IsVisible)
            Hide();
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
                parts.Add(BracketMetrics.ExtentOf(item.Box));
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
        var margin = _layer == OverlayLayer.Panel ? 4.0 : Margin;
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
