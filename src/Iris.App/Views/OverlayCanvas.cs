using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Social;

namespace Iris.App.Views;

/// <summary>
/// The surface the overlay is drawn on. It draws only in <see cref="OnRender"/> (no child elements), and only when the model or the
/// layout changes (<see cref="SetContent"/>); between changes WPF keeps the recorded drawing, so an idle overlay costs nothing.
/// Everything is drawn in physical pixels: a transform undoes the window's DPI scaling, so the model's pixel rectangles are used as they are.
/// </summary>
public sealed class OverlayCanvas : FrameworkElement
{
    private static readonly Brush DarkBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0A, 0x0F)));
    private static readonly Brush ToastFill = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x12, 0x1A)));
    private static readonly Brush[] ColorBrushes = new Brush[8];
    private static readonly Brush[] PanelFills = new Brush[8];

    private OverlayModel _model = OverlayModel.Empty;
    private PanelLayoutResult _panel = PanelLayoutResult.Empty;
    private ToastLayout? _toastLayout;
    private string? _toastText;

    static OverlayCanvas()
    {
        foreach (var color in Enum.GetValues<OverlayColor>())
        {
            var (r, g, b) = OverlayPalette.Rgb(color);
            ColorBrushes[(int)color] = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
            PanelFills[(int)color] = Frozen(new SolidColorBrush(Color.FromArgb(0x80, r, g, b))); // 50% opacity
        }
    }

    public OverlayCanvas()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Replaces what is drawn and redraws once.</summary>
    public void SetContent(OverlayModel model, PanelLayoutResult panel, ToastLayout? toastLayout, string? toastText)
    {
        _model = model;
        _panel = panel;
        _toastLayout = toastLayout;
        _toastText = toastText;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_model.IsEmpty && _toastText is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var undoDpi = new ScaleTransform(1.0 / dpi.DpiScaleX, 1.0 / dpi.DpiScaleY);
        undoDpi.Freeze();
        dc.PushTransform(undoDpi);
        try
        {
            foreach (var item in _model.Brackets)
                DrawBrackets(dc, item);
            DrawPanel(dc);
            DrawToast(dc);
        }
        finally
        {
            dc.Pop();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Brackets
    // ---------------------------------------------------------------------------------------

    private static void DrawBrackets(DrawingContext dc, BracketItem item)
    {
        var box = item.Box;
        var padding = box.Height * 0.12;                 // the brackets are a little taller than the nameplate
        var height = box.Height + 2 * padding;
        var top = box.Y - padding;
        var width = height * 0.24;
        var gap = box.Height * 0.18;                      // air between the nameplate and the bracket
        var stroke = Math.Max(2.0, height * 0.06);
        var outline = Math.Max(1.0, stroke * 0.5);        // the dark edge sticks out this far on each side of the colored line

        var left = BracketShape(item.Kind, box.X - gap - width, top, width, height, mirrored: false);
        var right = BracketShape(item.Kind, box.Right + gap, top, width, height, mirrored: true);

        var darkPen = RoundPen(DarkBrush, stroke + 2 * outline);
        var colorPen = RoundPen(ColorBrushes[(int)item.Color], stroke);

        // dark first (wider), then the color on top
        dc.DrawGeometry(null, darkPen, left);
        dc.DrawGeometry(null, darkPen, right);
        dc.DrawGeometry(null, colorPen, left);
        dc.DrawGeometry(null, colorPen, right);
    }

    private static Pen RoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze(); // frozen drawing resources are cheaper for WPF to keep and render (their brush is frozen too)
        return pen;
    }

    /// <summary>
    /// One bracket as an open line. The shape is described in a 0..1 box (u across, v down; u = 0 is the outer side, u = 1 faces the nameplate)
    /// and mapped into the rectangle; <paramref name="mirrored"/> flips it for the right-hand bracket.
    /// { } player list, [ ] org list, &lt; &gt; org attribute and No Match.
    /// </summary>
    private static Geometry BracketShape(MatchKind kind, double x, double y, double width, double height, bool mirrored)
    {
        Point P(double u, double v) => new(mirrored ? x + (1 - u) * width : x + u * width, y + v * height);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            switch (kind)
            {
                case MatchKind.PlayerList: // curly: end, spine, tip in the middle, spine, end
                    ctx.BeginFigure(P(1, 0), false, false);
                    ctx.QuadraticBezierTo(P(0.5, 0), P(0.5, 0.12), true, true);
                    ctx.LineTo(P(0.5, 0.38), true, true);
                    ctx.QuadraticBezierTo(P(0.5, 0.5), P(0, 0.5), true, true);
                    ctx.QuadraticBezierTo(P(0.5, 0.5), P(0.5, 0.62), true, true);
                    ctx.LineTo(P(0.5, 0.88), true, true);
                    ctx.QuadraticBezierTo(P(0.5, 1), P(1, 1), true, true);
                    break;

                case MatchKind.OrgList: // square
                    ctx.BeginFigure(P(1, 0), false, false);
                    ctx.LineTo(P(0, 0), true, true);
                    ctx.LineTo(P(0, 1), true, true);
                    ctx.LineTo(P(1, 1), true, true);
                    break;

                default: // angle: OrgAttribute and NoMatch
                    ctx.BeginFigure(P(1, 0), false, false);
                    ctx.LineTo(P(0, 0.5), true, true);
                    ctx.LineTo(P(1, 1), true, true);
                    break;
            }
        }
        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------------------------------
    // Panel
    // ---------------------------------------------------------------------------------------

    private void DrawPanel(DrawingContext dc)
    {
        var panel = _panel;
        if (panel.IsEmpty)
            return;

        var edge = Math.Max(1.0, panel.FontSize * 0.08);           // dark edge around the letters, on each side
        var edgePen = RoundPen(DarkBrush, edge * 2);

        foreach (var slot in panel.Slots)
        {
            var r = slot.Rect;
            dc.DrawRoundedRectangle(PanelFills[(int)slot.Entry.Color], null, new Rect(r.X, r.Y, r.Width, r.Height), panel.CornerRadius, panel.CornerRadius);

            var textX = r.X + panel.PaddingX;
            var textWidth = Math.Max(1.0, r.Width - 2 * panel.PaddingX);
            DrawOutlinedText(dc, slot.Entry.Handle, textX, r.Y + panel.PaddingY, textWidth, panel, edgePen);
            DrawOutlinedText(dc, slot.Entry.OrgsLine, textX, r.Y + panel.PaddingY + panel.LineHeight, textWidth, panel, edgePen);
        }

        if (panel.MoreText is { } more)
        {
            var r = panel.MoreRect;
            DrawOutlinedText(dc, more, r.X + panel.PaddingX, r.Y, Math.Max(1.0, r.Width - 2 * panel.PaddingX), panel, edgePen);
        }
    }

    /// <summary>White text with a dark edge: the letters become a shape; the dark outline is drawn first, the white fill on top of it.</summary>
    private static void DrawOutlinedText(
        DrawingContext dc, string text, double x, double lineTop, double maxWidth, PanelLayoutResult panel, Pen edgePen, bool center = false)
    {
        var size = panel.FontSize;
        var formatted = Format(text, size);
        if (formatted.Width > maxWidth && formatted.Width > 0)
        {
            // too wide for the box: shrink the text a little (down to 70%); anything still too wide is cut with "..." below
            size *= Math.Max(0.7, maxWidth / formatted.Width);
            formatted = Format(text, size);
        }
        formatted.MaxTextWidth = maxWidth;
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;

        if (center)
            x += Math.Max(0, maxWidth - formatted.WidthIncludingTrailingWhitespace) / 2;
        var origin = new Point(x, lineTop + (panel.LineHeight - formatted.Height) / 2);
        var shape = formatted.BuildGeometry(origin);
        if (shape is null)
            return;
        dc.DrawGeometry(null, edgePen, shape);
        dc.DrawGeometry(Brushes.White, null, shape);
    }

    /// <summary>The short confirmation ("Overlay off"): white outlined text, centered, on a dark translucent rounded box.</summary>
    private void DrawToast(DrawingContext dc)
    {
        if (_toastText is null || _toastLayout is null)
            return;

        var t = _toastLayout;
        var r = t.Rect;
        dc.DrawRoundedRectangle(ToastFill, null, new Rect(r.X, r.Y, r.Width, r.Height), t.CornerRadius, t.CornerRadius);

        // DrawOutlinedText takes its measurements from a panel layout; a one-line stand-in carries the toast's
        var measures = new PanelLayoutResult(Array.Empty<PanelSlot>(), null, default, t.FontSize, t.LineHeight, t.PaddingX, t.PaddingY, t.CornerRadius);
        var edgePen = RoundPen(DarkBrush, Math.Max(1.0, t.FontSize * 0.08) * 2);
        DrawOutlinedText(dc, _toastText, r.X + t.PaddingX, r.Y + t.PaddingY, Math.Max(1.0, r.Width - 2 * t.PaddingX), measures, edgePen, center: true);
    }

    /// <summary>
    /// The text is measured and drawn in physical pixels (the canvas pushes a transform that undoes the window's DPI scaling), so one device-independent
    /// unit of this text is exactly one pixel: pixelsPerDip is 1.0, not the window's value.
    /// </summary>
    private static FormattedText Format(string text, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayFonts.Bold, size, Brushes.White, 1.0);

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
