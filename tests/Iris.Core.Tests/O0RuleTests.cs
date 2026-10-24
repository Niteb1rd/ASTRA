using System.Text.Json;
using Iris.Core.Detection.Letters;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The sub-pixel O/0 rule (detector_ref.o0_measure / o0_prob / o0_verdict / glyph_box). The glyphs here are SYNTHETIC rings; the expected probabilities come from running the
/// Python formulas on made-up sizes. The check against real glyph crops is <see cref="O0LocalParityTests"/> (local only, it needs a folder that is not in the repository).
/// </summary>
public class O0RuleTests
{
    /// <summary>A ring at 8 times the screen size: a block of ink <paramref name="outerW"/> x <paramref name="outerH"/> with a hole <paramref name="holeW"/> x <paramref name="holeH"/> in the middle, in a margin of blank pixels.</summary>
    private static (byte[] Mask, int Width, int Height) Ring(int outerW, int outerH, int holeW, int holeH, int margin = 24)
    {
        var width = outerW + 2 * margin;
        var height = outerH + 2 * margin;
        var mask = new byte[width * height];
        for (var y = margin; y < margin + outerH; y++)
            for (var x = margin; x < margin + outerW; x++)
                mask[y * width + x] = 1;
        var hx = margin + (outerW - holeW) / 2;
        var hy = margin + (outerH - holeH) / 2;
        for (var y = hy; y < hy + holeH; y++)
            for (var x = hx; x < hx + holeW; x++)
                mask[y * width + x] = 0;
        return (mask, width, height);
    }

    [Fact]
    public void TheRuleNumbers_AreTheOnesOfTheRuleFile()
    {
        Assert.Equal(0.5667556378738614, O0Rule.WeightA);
        Assert.Equal(0.8238859429181884, O0Rule.WeightB);
        Assert.Equal(0.7533676849758579, O0Rule.MeanO);
        Assert.Equal(0.025796386202400184, O0Rule.SdO);
        Assert.Equal(0.6808192767983506, O0Rule.Mean0);
        Assert.Equal(0.01720362317305907, O0Rule.Sd0);
        Assert.Equal(0.95, O0Rule.Decide);
        Assert.Equal(8, O0Rule.Upscale);
        Assert.Equal(3, O0Rule.Pad);
    }

    [Fact]
    public void MeasureMask_FindsTheOuterSizeAndTheHole()
    {
        var (mask, w, h) = Ring(72, 96, 40, 64);

        var m = O0Rule.MeasureMask(mask, w, h)!.Value;

        Assert.Equal(9.0, m.OuterWidth);
        Assert.Equal(12.0, m.OuterHeight);
        Assert.Equal(5.0, m.HoleWidth);
        Assert.Equal(8.0, m.HoleHeight);
    }

    [Fact]
    public void MeasureMask_TheRunWithTheMostInkIsTheGlyph_ANeighboursEdgeInTheMarginIsIgnored()
    {
        var (mask, w, h) = Ring(72, 96, 40, 64, margin: 40);
        for (var y = 30; y < 100; y++)          // a thin bar of ink at the left edge of the cut: the edge of the neighbouring letter
            for (var x = 2; x < 6; x++)
                mask[y * w + x] = 1;

        var m = O0Rule.MeasureMask(mask, w, h)!.Value;

        Assert.Equal(9.0, m.OuterWidth);
        Assert.Equal(12.0, m.OuterHeight);
    }

    [Fact]
    public void MeasureMask_TheHoleIsTheLargestBackgroundThatTouchesNoEdge()
    {
        // an 8 shape: two holes, the lower one larger: the largest interior region is measured
        var (mask, w, h) = Ring(72, 96, 40, 64);
        for (var y = 42; y < 46; y++)   // a bar across the hole (hole rows 40..103): an upper hole 2 rows high, a lower one 58 rows high
            for (var x = 24; x < 24 + 72; x++)
                mask[y * w + x] = 1;

        var m = O0Rule.MeasureMask(mask, w, h)!.Value;

        Assert.Equal(40 / 8.0, m.HoleWidth);
        Assert.Equal(58 / 8.0, m.HoleHeight);
    }

    [Fact]
    public void MeasureMask_NoRing_NoMeasurement()
    {
        var bar = new byte[40 * 100];
        for (var y = 2; y < 98; y++)
            for (var x = 15; x < 23; x++)
                bar[y * 40 + x] = 1;       // the letter I: no hole
        Assert.Null(O0Rule.MeasureMask(bar, 40, 100));

        var open = Ring(72, 96, 40, 64);   // a C: the hole touches the right edge
        for (var y = 24 + 16; y < 24 + 80; y++)
            for (var x = 24 + 56; x < 24 + 72; x++)
                open.Mask[y * open.Width + x] = 0;
        Assert.Null(O0Rule.MeasureMask(open.Mask, open.Width, open.Height));

        Assert.Null(O0Rule.MeasureMask(new byte[30 * 30], 30, 30)); // nothing at all
    }

    [Fact]
    public void MeasureMask_ABoxMuchWiderThanHighIsNotOneGlyph()
    {
        var (mask, w, h) = Ring(200, 96, 120, 64); // two letters run together: 200 > 1.6 * 96

        Assert.Null(O0Rule.MeasureMask(mask, w, h));
    }

    [Theory]
    [InlineData(9.0, 12.0, 5.0, 8.0, 0.9997938311463952)]   // above the O mean: clamped to it
    [InlineData(8.0, 12.0, 4.0, 8.0, 0.012620097629904944)]  // below the 0 mean: clamped to it
    [InlineData(8.5, 12.0, 4.5, 8.0, 0.4224919265388571)]
    [InlineData(7.5, 12.0, 4.0, 9.0, 0.012620097629904944)]  // below the 0 mean: clamped to it
    [InlineData(8.75, 12.0, 4.5, 8.0, 0.8533261738581932)]
    public void ProbabilityO_IsThePythonFormula(double ow, double oh, double hw, double hh, double expected) =>
        Assert.Equal(expected, O0Rule.ProbabilityO(new O0Measurement(ow, oh, hw, hh)), 9);

    [Fact]
    public void Verdict_NeedsAtLeast95PercentEitherWay()
    {
        Assert.Equal('O', O0Rule.Verdict(0.95));
        Assert.Equal('O', O0Rule.Verdict(0.99));
        Assert.Null(O0Rule.Verdict(0.9499));
        Assert.Null(O0Rule.Verdict(0.5));
        Assert.Null(O0Rule.Verdict(0.0501));
        Assert.Equal('0', O0Rule.Verdict(0.05));
        Assert.Equal('0', O0Rule.Verdict(0.001));
    }

    [Fact]
    public void GlyphBox_MapsTheCutColumnsAndInkRowsBackToFramePixelsWithThePad()
    {
        var data = new byte[24 * 40];
        for (var y = 4; y < 20; y++)               // ink rows 4..19, columns 8..19
            for (var x = 8; x < 20; x++)
                data[y * 40 + x] = 1;
        var mask = new LineMask(24, 40, data) { OriginX = 100, OriginY = 200, ColumnOffset = 6, RowOffset = 10 };

        var box = O0Rule.GlyphBox(mask, 8, 20)!.Value;

        // x0 = 100 + floor((6 + 8) / 4) - 3, x1 = 100 + ceil((6 + 20) / 4) + 3, y0 = 200 + floor((10 + 4) / 4) - 3, y1 = 200 + ceil((10 + 19 + 1) / 4) + 3
        Assert.Equal((100, 200, 110, 211), box);
        Assert.Null(O0Rule.GlyphBox(mask, 24, 30)); // no ink in these columns
    }

    // ----- with the native OpenCV library: the upscale, the threshold and the correction of a read -----

    /// <summary>A ring of ink 255 on a dark background, hard pixels, <paramref name="margin"/> pixels of margin; the box of the ring is at (margin, margin).</summary>
    private static Mat DrawnRing(int outerW, int outerH, int holeW, int holeH, int margin = 3)
    {
        var image = new Mat(outerH + 2 * margin, outerW + 2 * margin, MatType.CV_8UC3, new Scalar(20, 20, 20));
        Cv2.Rectangle(image, new Rect(margin, margin, outerW, outerH), new Scalar(255, 230, 110), -1);
        Cv2.Rectangle(image, new Rect(margin + (outerW - holeW) / 2, margin + (outerH - holeH) / 2, holeW, holeH), new Scalar(20, 20, 20), -1);
        return image;
    }

    [OpenCvFact]
    public void Measure_AWideRingIsAnO_ANarrowOneIsAZero()
    {
        using var wide = DrawnRing(10, 12, 6, 8);
        using var narrow = DrawnRing(6, 12, 2, 8);

        var o = O0Rule.Measure(wide)!.Value;
        var zero = O0Rule.Measure(narrow)!.Value;

        Assert.InRange(o.OuterWidth, 9.5, 10.5);
        Assert.InRange(o.OuterHeight, 11.5, 12.5);
        Assert.InRange(o.HoleWidth, 5.5, 6.5);
        Assert.Equal('O', O0Rule.Verdict(O0Rule.ProbabilityO(o)));
        Assert.InRange(zero.OuterWidth, 5.5, 6.5);
        Assert.Equal('0', O0Rule.Verdict(O0Rule.ProbabilityO(zero)));
    }

    [OpenCvFact]
    public void Measure_AGrayCutGivesTheSameAsAColorCut()
    {
        using var color = DrawnRing(9, 12, 5, 8);
        using var gray = new Mat();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);

        var a = O0Rule.Measure(color)!.Value;
        var b = O0Rule.Measure(gray)!.Value;

        Assert.InRange(Math.Abs(a.OuterWidth - b.OuterWidth), 0, 0.25);
        Assert.InRange(Math.Abs(a.HoleWidth - b.HoleWidth), 0, 0.25);
    }

    [OpenCvFact]
    public void Measure_ALetterWithoutAHoleIsNotMeasured()
    {
        using var image = new Mat(18, 9, MatType.CV_8UC3, new Scalar(20, 20, 20));
        Cv2.Rectangle(image, new Rect(3, 3, 2, 12), new Scalar(255, 255, 255), -1); // I

        Assert.Null(O0Rule.Measure(image));
    }

    /// <summary>A line mask of one glyph at 4x whose geometry maps it onto the ring drawn at (<paramref name="x"/>, <paramref name="y"/>) of the frame.</summary>
    private static PieceRead ReadOf(string text, int x, int y, int glyphWidth, int glyphHeight)
    {
        var data = Enumerable.Repeat((byte)1, glyphWidth * 4 * glyphHeight * 4).ToArray();
        var mask = new LineMask(glyphHeight * 4, glyphWidth * 4, data) { OriginX = x, OriginY = y };
        return new PieceRead(Array.Empty<ReadPiece>(), glyphHeight * 4, new[] { (0, glyphWidth * 4) }, mask);
    }

    [OpenCvFact]
    public void Correct_ReplacesTheModelsCharacterWhenTheRuleIsSure()
    {
        // a wide ring read as 0 is an O, a narrow ring read as O is a 0; the glyph's box is the ring itself, the pad of 3 pixels is the dark margin around it
        using var wide = DrawnRing(10, 12, 6, 8);
        using var narrow = DrawnRing(6, 12, 2, 8);

        Assert.Equal("O", O0Rule.Correct(wide, ReadOf("0", 3, 3, 10, 12), "0"));
        Assert.Equal("0", O0Rule.Correct(narrow, ReadOf("O", 3, 3, 6, 12), "O"));
    }

    [OpenCvFact]
    public void Correct_KeepsTheModelsCharacterWhenItIsNoOrZeroOrThereAreNoSpans()
    {
        using var ring = DrawnRing(9, 12, 5, 8);
        var read = ReadOf("A", 3, 3, 9, 12);

        Assert.Equal("A", O0Rule.Correct(ring, read, "A")); // a letter that is no O or 0 is never touched, whatever the pixels look like
        Assert.Equal("O", O0Rule.Correct(ring, new PieceRead(Array.Empty<ReadPiece>(), 48), "O")); // no spans recorded: left as read
    }

    [OpenCvFact]
    public void Correct_TheSpansMustMatchTheTextOneToOne()
    {
        using var wide = DrawnRing(10, 12, 6, 8);
        var read = ReadOf("0", 3, 3, 10, 12);

        Assert.Equal("00", O0Rule.Correct(wide, read, "00")); // two characters, one span: a cut failed, the line is left as read
    }
}

/// <summary>
/// LOCAL ONLY parity of the C# measurement with the Python reference on REAL glyph crops: the folder named by the environment variable <c>IRIS_O0_FIXTURES</c> (the training kit's
/// <c>fixtures-o0</c>) holds <c>caseNN.png</c> (exactly what <c>o0_measure</c> receives: the glyph's box plus 3 pixels of margin, BGR, original pixels) and <c>caseNN.json</c> (the expected
/// outer_w, outer_h, hole_w, hole_h, p_O and verdict; null for a glyph that is not a ring). The crops come from game footage and are never committed; without the variable the test is skipped.
/// Sizes must agree within 0.125 px (one upscaled pixel), P(O) within 0.02, and the verdict exactly.
/// </summary>
public class O0LocalParityTests
{
    private const double SizeTolerance = 0.125; // one upscaled pixel (the reference ran on another OpenCV version)
    private const double ProbabilityTolerance = 0.02;

    [LocalO0FixtureFact]
    public void Measurements_MatchPythonOnTheRealGlyphs()
    {
        var folder = LocalO0FixtureFactAttribute.Folder!;
        var cases = Directory.GetFiles(folder, "case*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(cases);
        var problems = new List<string>();
        foreach (var jsonPath in cases)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var e = doc.RootElement;
            var name = e.GetProperty("case").GetString()!;
            var png = Path.Combine(folder, e.GetProperty("png").GetString()!);
            using var crop = Cv2.ImRead(png, ImreadModes.Color);
            if (crop.Empty())
            {
                problems.Add($"{name}: cannot read {png}");
                continue;
            }

            var measurement = O0Rule.Measure(crop);
            if (e.GetProperty("outer_w").ValueKind == JsonValueKind.Null)
            {
                if (measurement is not null)
                    problems.Add($"{name}: measured {measurement}, Python found no ring");
                continue;
            }

            if (measurement is not { } m)
            {
                problems.Add($"{name}: no measurement, Python found a ring");
                continue;
            }

            Check(problems, name, "outer_w", m.OuterWidth, e.GetProperty("outer_w").GetDouble(), SizeTolerance);
            Check(problems, name, "outer_h", m.OuterHeight, e.GetProperty("outer_h").GetDouble(), SizeTolerance);
            Check(problems, name, "hole_w", m.HoleWidth, e.GetProperty("hole_w").GetDouble(), SizeTolerance);
            Check(problems, name, "hole_h", m.HoleHeight, e.GetProperty("hole_h").GetDouble(), SizeTolerance);
            var p = O0Rule.ProbabilityO(m);
            Check(problems, name, "P(O)", p, e.GetProperty("p_O").GetDouble(), ProbabilityTolerance);
            var expectedVerdict = e.GetProperty("verdict").ValueKind == JsonValueKind.Null ? (char?)null : e.GetProperty("verdict").GetString()![0];
            if (O0Rule.Verdict(p) != expectedVerdict)
                problems.Add($"{name}: verdict '{O0Rule.Verdict(p)}' (P={p:F4}), Python '{expectedVerdict}'");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static void Check(List<string> problems, string name, string what, double actual, double expected, double tolerance)
    {
        if (Math.Abs(actual - expected) > tolerance)
            problems.Add($"{name}: {what} {actual:F4}, Python {expected:F4}");
    }
}
