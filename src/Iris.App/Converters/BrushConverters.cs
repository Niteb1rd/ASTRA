using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Iris.App.ViewModels;
using Iris.Core.Config;

namespace Iris.App.Converters;

/// <summary>Turns an <see cref="OverlayColor"/> into the brush of its swatch in the drop-down.</summary>
public sealed class OverlayColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is OverlayColor color ? BrushFor(color) : Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    /// <summary>The swatch colors shown in the config window. (The real overlay colors are decided in the overlay increment.)</summary>
    public static Brush BrushFor(OverlayColor color) => color switch
    {
        OverlayColor.Red => Frozen(0xE5, 0x39, 0x35),
        OverlayColor.Orange => Frozen(0xFB, 0x8C, 0x00),
        OverlayColor.Yellow => Frozen(0xFD, 0xD8, 0x35),
        OverlayColor.Green => Frozen(0x43, 0xA0, 0x47),
        OverlayColor.Cyan => Frozen(0x00, 0xAC, 0xC1),
        OverlayColor.Blue => Frozen(0x1E, 0x88, 0xE5),
        OverlayColor.Magenta => Frozen(0xE0, 0x40, 0xC8),
        OverlayColor.Purple => Frozen(0x8E, 0x24, 0xAA),
        _ => Brushes.Gray,
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Turns a row's <see cref="RowStatusKind"/> into the text color of its Status cell.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    private static readonly Brush Green = Make(0x2E, 0x7D, 0x32);
    private static readonly Brush Red = Make(0xC6, 0x28, 0x28);
    private static readonly Brush Amber = Make(0xB2, 0x6A, 0x00);
    private static readonly Brush Grey = Make(0x61, 0x61, 0x61);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RowStatusKind kind)
            return Grey;
        return kind switch
        {
            RowStatusKind.Valid => Green,
            RowStatusKind.NotFound => Red,
            RowStatusKind.Invalid => Red,
            RowStatusKind.Failed => Amber,
            _ => Grey,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
