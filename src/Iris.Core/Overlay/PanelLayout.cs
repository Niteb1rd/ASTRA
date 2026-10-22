using Iris.Core.Settings;

namespace Iris.Core.Overlay;

/// <summary>One panel entry and the rectangle it is drawn in (game-window pixels).</summary>
public sealed record PanelSlot(PanelEntry Entry, PixelRect Rect);

/// <summary>
/// Where everything of the panel goes, plus the measurements the painter needs.
/// <see cref="MoreText"/> ("+3 more") is set when not every entry fit; it is drawn as a last text line at <see cref="MoreRect"/>, without a box.
/// </summary>
public sealed record PanelLayoutResult(
    IReadOnlyList<PanelSlot> Slots,
    string? MoreText,
    PixelRect MoreRect,
    double FontSize,
    double LineHeight,
    double PaddingX,
    double PaddingY,
    double CornerRadius)
{
    public static readonly PanelLayoutResult Empty = new(Array.Empty<PanelSlot>(), null, default, 0, 0, 0, 0, 0);

    public bool IsEmpty => Slots.Count == 0 && MoreText is null;
}

/// <summary>Where the short on-screen confirmation box ("Overlay off") goes: the top of the panel area, one line high.</summary>
public sealed record ToastLayout(PixelRect Rect, double FontSize, double LineHeight, double PaddingX, double PaddingY, double CornerRadius);

/// <summary>
/// Computes the right-hand panel's rectangles from the settings and the game window size:
/// - the panel's right edge is the window width minus <c>panel_right_gap_percent</c> of the width; its width is <c>panel_width_percent</c> of the width;
/// - its top is <c>panel_top_gap_percent</c> of the height;
/// - entries are stacked downward, each its own rectangle (two text lines), with a gap between rectangles;
/// - nothing is drawn into the bottom 10% of the window; if the entries do not all fit, the ones that do are shown and the last line says "+N more".
/// Text size: <c>panel_text_size</c> is in pixels at 1440p and scales linearly with the window height.
/// </summary>
public static class PanelLayout
{
    /// <summary>The panel never reaches into this share of the window height at the bottom.</summary>
    public const double BottomReservePercent = 10;

    /// <summary>The window height the text size setting refers to.</summary>
    public const double ReferenceHeight = 1440;

    /// <summary>The text size in pixels for a window of the given height.</summary>
    public static double ScaledTextSize(int textSizeAt1440, double windowHeight) => textSizeAt1440 * windowHeight / ReferenceHeight;

    public static PanelLayoutResult Compute(IReadOnlyList<PanelEntry> entries, double windowWidth, double windowHeight, AppSettings settings)
    {
        if (!settings.PanelEnabled || entries.Count == 0 || windowWidth <= 0 || windowHeight <= 0)
            return PanelLayoutResult.Empty;

        var fontSize = ScaledTextSize(settings.PanelTextSize, windowHeight);
        var lineHeight = LineHeightFor(fontSize);
        var paddingX = PaddingXFor(fontSize);
        var paddingY = PaddingYFor(fontSize);
        var corner = CornerFor(fontSize);
        var entryHeight = 2 * lineHeight + 2 * paddingY;
        var gap = fontSize * 0.4;

        var panelWidth = windowWidth * settings.PanelWidthPercent / 100.0;
        var right = windowWidth - windowWidth * settings.PanelRightGapPercent / 100.0;
        var left = Math.Max(0, right - panelWidth);
        var top = windowHeight * settings.PanelTopGapPercent / 100.0;
        var limit = windowHeight - windowHeight * BottomReservePercent / 100.0;

        // How many entries are shown: all if they fit; otherwise as many as fit together with the "+N more" line.
        var total = entries.Count;
        var shown = total;
        var allHeight = total * entryHeight + (total - 1) * gap;
        if (top + allHeight > limit)
        {
            shown = 0;
            for (var k = total - 1; k >= 0; k--)
            {
                if (top + k * (entryHeight + gap) + lineHeight <= limit)
                {
                    shown = k;
                    break;
                }
            }
        }

        var slots = new List<PanelSlot>(shown);
        for (var i = 0; i < shown; i++)
            slots.Add(new PanelSlot(entries[i], new PixelRect(left, top + i * (entryHeight + gap), panelWidth, entryHeight)));

        string? moreText = null;
        var moreRect = default(PixelRect);
        if (shown < total)
        {
            var moreTop = top + shown * (entryHeight + gap);
            if (moreTop + lineHeight <= limit)
            {
                moreText = $"+{total - shown} more";
                moreRect = new PixelRect(left, moreTop, panelWidth, lineHeight);
            }
        }

        return new PanelLayoutResult(slots, moreText, moreRect, fontSize, lineHeight, paddingX, paddingY, corner);
    }

    private static double LineHeightFor(double fontSize) => fontSize * 1.25;

    private static double PaddingXFor(double fontSize) => fontSize * 0.7;

    private static double PaddingYFor(double fontSize) => fontSize * 0.45;

    private static double CornerFor(double fontSize) => fontSize * 0.5;

    /// <summary>
    /// The box for the on-screen confirmation text: same left edge, width and top as the panel, one text line high, same text size.
    /// It does not depend on <c>panel_enabled</c> (the confirmation also shows while the panel is off). Null for an unusable window size.
    /// </summary>
    public static ToastLayout? ComputeToast(double windowWidth, double windowHeight, AppSettings settings)
    {
        if (windowWidth <= 0 || windowHeight <= 0)
            return null;

        var fontSize = ScaledTextSize(settings.PanelTextSize, windowHeight);
        var lineHeight = LineHeightFor(fontSize);
        var paddingY = PaddingYFor(fontSize);
        var panelWidth = windowWidth * settings.PanelWidthPercent / 100.0;
        var right = windowWidth - windowWidth * settings.PanelRightGapPercent / 100.0;
        var left = Math.Max(0, right - panelWidth);
        var top = windowHeight * settings.PanelTopGapPercent / 100.0;
        return new ToastLayout(
            new PixelRect(left, top, panelWidth, lineHeight + 2 * paddingY),
            fontSize, lineHeight, PaddingXFor(fontSize), paddingY, CornerFor(fontSize));
    }
}
