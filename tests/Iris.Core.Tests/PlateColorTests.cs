using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The plate colour classes (detector_ref.plate_color / classify_color): the median colour of the BRIGHTER HALF of the text pixels, then WHITE (with the light cyan), BLUE, GREEN or RED.
/// Pure logic: masks are built by hand with the colours of their ink pixels, so no OpenCV is needed. The parity with Python on drawn plates is in <see cref="PlateStackTests"/>.
/// </summary>
public class PlateColorTests
{
    /// <summary>A mask of <paramref name="count"/> ink pixels in one row, each with the given (B, G, R) colour.</summary>
    private static LineMask Ink(int count, params (byte B, byte G, byte R)[] colors)
    {
        var data = Enumerable.Repeat((byte)1, count).ToArray();
        var ink = new byte[count * 3];
        for (var i = 0; i < count; i++)
        {
            var c = colors[i % colors.Length];
            ink[i * 3] = c.B;
            ink[i * 3 + 1] = c.G;
            ink[i * 3 + 2] = c.R;
        }

        return new LineMask(1, count, data) { InkColors = ink };
    }

    // light cyan of a normal plate (RGB 110, 230, 255), the party blue and hostile red (as drawn in the fixtures)
    private static readonly (byte B, byte G, byte R) Cyan = (255, 230, 110);
    private static readonly (byte B, byte G, byte R) Blue = (240, 190, 150);
    private static readonly (byte B, byte G, byte R) Red = (70, 70, 235);

    [Theory]
    [InlineData(0.0, 0.0, PlateColor.White)]      // pure white
    [InlineData(240.0, 0.14, PlateColor.White)]   // a tint below the white limit
    [InlineData(184.0, 0.40, PlateColor.White)]   // light cyan: below the blue range
    [InlineData(184.0, 0.57, PlateColor.White)]   // saturated cyan is still not green, red or blue
    [InlineData(213.0, 0.37, PlateColor.Blue)]
    [InlineData(195.0, 0.25, PlateColor.Blue)]    // the edges of the blue range
    [InlineData(269.9, 0.60, PlateColor.Blue)]
    [InlineData(213.0, 0.24, PlateColor.White)]   // blue hue but too weak
    [InlineData(270.0, 0.60, PlateColor.White)]   // purple: nothing
    [InlineData(120.0, 0.64, PlateColor.Green)]
    [InlineData(40.0, 0.45, PlateColor.Green)]
    [InlineData(164.9, 0.60, PlateColor.Green)]
    [InlineData(120.0, 0.44, PlateColor.White)]   // green hue but not saturated enough
    [InlineData(358.0, 0.97, PlateColor.Red)]
    [InlineData(0.0, 0.70, PlateColor.Red)]
    [InlineData(330.0, 0.45, PlateColor.Red)]
    [InlineData(39.9, 0.60, PlateColor.Red)]      // red wraps around 0
    [InlineData(329.9, 0.60, PlateColor.White)]
    public void Classify_UsesTheThresholdsOfTheReference(double hue, double saturation, PlateColor expected) =>
        Assert.Equal(expected, PlateColors.Classify(hue, saturation));

    [Fact]
    public void Classify_UsesTheSettingsGiven()
    {
        var strict = new PlateColorSettings { WhiteSaturation = 0.5 };

        Assert.Equal(PlateColor.White, PlateColors.Classify(213.0, 0.37, strict));
        Assert.Equal(PlateColor.Blue, PlateColors.Classify(213.0, 0.37));
    }

    [Theory]
    [InlineData(255, 230, 110, PlateColor.White)]
    [InlineData(255, 255, 255, PlateColor.White)]
    [InlineData(240, 190, 150, PlateColor.Blue)]
    [InlineData(80, 185, 80, PlateColor.Green)]
    [InlineData(70, 70, 235, PlateColor.Red)]
    public void AFlatColoredText_IsClassified(byte b, byte g, byte r, PlateColor expected)
    {
        var reading = PlateColors.Classify(new[] { Ink(100, (b, g, r)) });

        Assert.Equal(expected, reading!.Value.Color);
    }

    [Fact]
    public void TheColorIsTheMedianOfTheBrighterHalf_SoTheDarkEdgesOfTheGlyphsDoNotPullItToTheBackground()
    {
        // 60 text pixels of the blue and 40 dark, blended edge pixels (the glyph edge mixed with a dark background)
        var edge = ((byte)40, (byte)30, (byte)25);
        var mask = Ink(100, Enumerable.Repeat(Blue, 6).Concat(Enumerable.Repeat(edge, 4)).ToArray());

        var stats = PlateColors.Measure(new[] { mask })!.Value;

        Assert.Equal(100, stats.Pixels);
        Assert.Equal(PlateColor.Blue, PlateColors.Classify(stats.Hue, stats.Saturation));
        Assert.InRange(stats.Hue, 211, 215);
        Assert.InRange(stats.Saturation, 0.36, 0.38);
        Assert.Equal(240 / 255.0, stats.Value, 6);
    }

    [Fact]
    public void TheRowsOfAPlateAreMeasuredTogether()
    {
        var upper = Ink(60, Red);
        var lower = Ink(60, Red);

        var reading = PlateColors.Classify(new[] { upper, lower });

        Assert.Equal(PlateColor.Red, reading!.Value.Color);
        Assert.Equal(120, PlateColors.Measure(new[] { upper, lower })!.Value.Pixels);
    }

    [Fact]
    public void TheHueAndSaturationFollowTheHsvFormulas()
    {
        // RGB (110, 230, 255): max is blue, hue = 60 * ((r - g) / (mx - mn)) + 240 = 60 * (-120 / 145) + 240; saturation = 145 / 255
        var stats = PlateColors.Measure(new[] { Ink(50, Cyan) })!.Value;

        Assert.Equal(60.0 * (-120.0 / 145.0) + 240.0, stats.Hue, 9);
        Assert.Equal(145.0 / 255.0, stats.Saturation, 9);
        // red max: the hue is taken modulo 360 and never negative (RGB 235, 70, 80: g - b is negative)
        var red = PlateColors.Measure(new[] { Ink(50, ((byte)80, (byte)70, (byte)235)) })!.Value;
        Assert.InRange(red.Hue, 350, 360);
    }

    [Fact]
    public void TheMedianOfAnEvenCountIsTheMeanOfTheMiddleTwo()
    {
        // brightest channel: 100 pixels, the brighter half (value >= the element at index 50) is the 50 pixels of 200 or more; blue channel alternates 200 and 220
        var mask = Ink(100, ((byte)200, (byte)10, (byte)10), ((byte)220, (byte)10, (byte)10), ((byte)30, (byte)10, (byte)10), ((byte)30, (byte)10, (byte)10));

        var stats = PlateColors.Measure(new[] { mask })!.Value;

        // core = the 50 bright pixels: 25 of blue 200 and 25 of blue 220: the median is the mean of the two middle values, 210; r = g = 10
        Assert.Equal(210.0 / 255.0, stats.Value, 9);
        Assert.Equal((210.0 - 10.0) / 210.0, stats.Saturation, 9);
    }

    [Fact]
    public void TooFewTextPixels_GiveNoColor()
    {
        Assert.Null(PlateColors.Measure(new[] { Ink(PlateColors.MinPixels - 1, Red) }));
        Assert.Null(PlateColors.Classify(new[] { Ink(10, Red) }));
        Assert.NotNull(PlateColors.Measure(new[] { Ink(PlateColors.MinPixels, Red) }));
        Assert.Null(PlateColors.Measure(new[] { new LineMask(1, 1, new byte[] { 1 }) })); // a mask made without keepColors
    }
}
