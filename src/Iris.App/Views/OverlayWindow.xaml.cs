using System;
using System.Windows;
using System.Windows.Interop;
using Iris.App.Services;
using Iris.Core.Overlay;
using Iris.Core.Settings;

namespace Iris.App.Views;

/// <summary>
/// The overlay: a transparent, always-on-top window that lies exactly over the game window (or a monitor, for the test overlay).
/// After the window exists it gets the extended styles "layered, transparent, tool window, no activate": mouse clicks go through it to the game,
/// it never takes the keyboard focus and it is not listed in Alt+Tab. It is created once and only hidden when not needed.
/// <see cref="Present"/> is cheap to call again and again: nothing is moved or redrawn unless the model, the size or the settings changed.
///
/// Cost to remember for Phase 3: the window is a layered (transparent) window as large as the whole game window. For such a window Windows
/// composites and updates the full surface (e.g. 2560x1440 or 3840x2160 pixels) on every redraw, however little changed in it. That is fine
/// while the overlay redraws rarely (panel, confirmation) but not at the capture rate: Phase 3 must either throttle the redraws (only when the
/// detections changed, with a cap per second) or shrink the window to the area that is actually drawn.
/// </summary>
public partial class OverlayWindow : Window
{
    private IntPtr _hwnd = IntPtr.Zero;
    private PixelRect _bounds;
    private bool _hasBounds;
    private OverlayModel? _model;
    private AppSettings? _settings;
    private string? _toast;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle, new IntPtr(style));

        ApplyBounds();
    }

    /// <summary>
    /// Moving the window to another monitor can change its DPI, and WPF then resizes it to what Windows suggests.
    /// The size must stay exactly the game window's, so it is set again afterwards.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyBounds();
        Surface.InvalidateVisual();
    }

    /// <summary>
    /// Shows <paramref name="model"/> over <paramref name="bounds"/> (screen pixels). The model's coordinates are relative to the top-left
    /// corner of that rectangle. Shows the window if it is hidden. <paramref name="toast"/> is a short confirmation text drawn just above the panel's top.
    /// </summary>
    public void Present(OverlayModel model, PixelRect bounds, AppSettings settings, string? toast = null)
    {
        var boundsChanged = !_hasBounds || bounds != _bounds;
        var contentChanged = boundsChanged || !ReferenceEquals(model, _model) || settings != _settings || toast != _toast;

        _bounds = bounds;
        _hasBounds = true;
        _model = model;
        _settings = settings;
        _toast = toast;

        if (boundsChanged)
            ApplyBounds();

        if (contentChanged)
        {
            var layout = PanelLayout.Compute(model.Panel, bounds.Width, bounds.Height, settings);
            var toastLayout = toast is null ? null : PanelLayout.ComputeToast(bounds.Width, bounds.Height, settings);
            Surface.SetContent(model, layout, toastLayout, toastLayout is null ? null : toast);
        }

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

    /// <summary>Puts the window at its place in real pixels, on top of everything, without activating it. A DIP-based Left/Width would be off on scaled monitors.</summary>
    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero || !_hasBounds)
            return;
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HwndTopmost,
            (int)Math.Round(_bounds.X), (int)Math.Round(_bounds.Y),
            (int)Math.Round(_bounds.Width), (int)Math.Round(_bounds.Height),
            NativeMethods.SwpNoActivate);
    }
}
