using System.Text.Json;
using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The underscore / hyphen rule (detector_ref.bar_features / us_verdict / Reader.us_correct). The masks here are SYNTHETIC: a block of ink at a known height in a capital band of 100 rows
/// (top row 10, bottom row 109, so cap = 100 and a fraction is the row number minus 10 over 100), so every expected number can be checked by hand. The check against real line crops is
/// <see cref="UnderscoreHyphenLocalParityTests"/> (local only, it needs a folder that is not in the repository).
/// </summary>
public class UnderscoreHyphenRuleTests
{
    private const int Top = 10;
    private const int Bottom = 109; // inclusive: cap = 100
    private static readonly CapBand Band = new(Top, Bottom);

    /// <summary>A mask of <paramref name="width"/> x <paramref name="height"/> with a block of ink for every (first row, last row INCLUSIVE, first column, one past the last column).</summary>
    private static LineMask Mask(int width, int height, params (int Row0, int Row1, int Col0, int Col1)[] blocks)
    {
        var data = new byte[width * height];
        foreach (var (row0, row1, col0, col1) in blocks)
            for (var y = row0; y <= row1; y++)
                for (var x = col0; x < col1; x++)
                    data[y * width + x] = 1;
        return new LineMask(height, width, data);
    }

    /// <summary>A bar over columns 4 to 23 (20 columns), rows <paramref name="row0"/> to <paramref name="row1"/>.</summary>
    private static LineMask BarMask(int row0, int row1) => Mask(30, 130, (row0, row1, 4, 24));

    [Fact]
    public void TheRuleNumbers_AreTheOnesOfTheRuleFile()
    {
        Assert.Equal(0.87, UnderscoreHyphenRule.UnderscoreMin);
        Assert.Equal(0.71, UnderscoreHyphenRule.HyphenMax);
        Assert.Equal(0.45, UnderscoreHyphenRule.MaxThick);
        Assert.Equal(0.25, UnderscoreHyphenRule.RowFraction);
    }

    [Fact]
    public void TheRuleIsOnByDefault()
    {
        Assert.True(NameplateDetectorSettings.Default.UnderscoreHyphenRule);
        Assert.False((NameplateDetectorSettings.Default with { UnderscoreHyphenRule = false }).UnderscoreHyphenRule);
    }

    [Fact]
    public void Measure_FracIsTheCentreOfTheBarRowsInTheCapitalBand_RowsArePixelIntervals()
    {
        // rows 96 and 97: centre (96 + 97 + 1) / 2 = 97, (97 - 10) / 100 = 0.87
        var bar = UnderscoreHyphenRule.Measure(BarMask(96, 97), 0, 30, Band)!.Value;

        Assert.Equal(0.87, bar.Frac, 12);
        Assert.Equal(0.02, bar.Thick, 12);   // 2 rows of 100
        Assert.Equal(0.20, bar.Length, 12);  // 20 columns of 100
    }

    [Fact]
    public void Measure_TheBottomRowOfTheBandIsInclusive_SoTheBandIsOneHundredRows()
    {
        // a bar on the last row of the band (109): centre 109.5, (109.5 - 10) / 100 = 0.995
        var bar = UnderscoreHyphenRule.Measure(BarMask(109, 109), 0, 30, Band)!.Value;

        Assert.Equal(0.995, bar.Frac, 12);
        Assert.Equal(0.01, bar.Thick, 12);
    }

    // frac = (r0 + 1 - 10) / 100 for the two row bars below: the edges of the undecided band 0.71 .. 0.87 and both sides of them
    [Theory]
    [InlineData(96, "_")]   // 0.87  exactly the underscore limit: at least, so an underscore
    [InlineData(95, "")]    // 0.86  undecided: the model's character stays
    [InlineData(81, "")]    // 0.72  undecided
    [InlineData(80, "-")]   // 0.71  exactly the hyphen limit: at most, so a hyphen
    [InlineData(100, "_")]  // 0.91
    [InlineData(108, "_")]  // 0.99  the bottom of the band
    [InlineData(109, "_")]  // 1.00  below the band's bottom row (the real underscores sit at 1.0 to 1.1)
    [InlineData(56, "-")]   // 0.47  the middle of the band (the real hyphens sit at 0.58)
    [InlineData(30, "-")]   // 0.21  high bars are hyphens too (only 'at most 0.71' counts)
    public void Verdict_FollowsTheFractionOfTheCapitalBand(int row0, string expected)
    {
        var bar = UnderscoreHyphenRule.Measure(BarMask(row0, row0 + 1), 0, 30, Band)!.Value;

        char? want = expected.Length == 0 ? null : expected[0];

        Assert.Equal(want, UnderscoreHyphenRule.Verdict(bar));
    }

    [Fact]
    public void Verdict_ABarThickerThanFortyFivePercentOfTheCapsGetsNoVerdict()
    {
        // rows 80 to 124: 45 rows = 0.45 exactly, centre 102.5, frac 0.925: still a clean bar (thick above the limit is what withholds the verdict)
        var thin = UnderscoreHyphenRule.Measure(BarMask(80, 124), 0, 30, Band)!.Value;
        Assert.Equal(0.45, thin.Thick, 12);
        Assert.Equal('_', UnderscoreHyphenRule.Verdict(thin));

        // rows 80 to 125: 46 rows = 0.46, frac 0.93 would be an underscore, but it is a blob (a letter fragment or merged glyph)
        var blob = UnderscoreHyphenRule.Measure(BarMask(80, 125), 0, 30, Band)!.Value;
        Assert.Equal(0.46, blob.Thick, 12);
        Assert.Equal(0.93, blob.Frac, 12);
        Assert.Null(UnderscoreHyphenRule.Verdict(blob));

        // a thick bar in the hyphen position is no hyphen either
        var thickMiddle = UnderscoreHyphenRule.Measure(BarMask(30, 80), 0, 30, Band)!.Value; // 51 rows, centre 55.5, frac 0.455
        Assert.Null(UnderscoreHyphenRule.Verdict(thickMiddle));
    }

    [Fact]
    public void Verdict_ADirectCallWithTheEdgeNumbers()
    {
        Assert.Equal('_', UnderscoreHyphenRule.Verdict(new BarMeasurement(0.87, 0.2, 0.6)));
        Assert.Null(UnderscoreHyphenRule.Verdict(new BarMeasurement(0.8699, 0.2, 0.6)));
        Assert.Equal('-', UnderscoreHyphenRule.Verdict(new BarMeasurement(0.71, 0.2, 0.4)));
        Assert.Null(UnderscoreHyphenRule.Verdict(new BarMeasurement(0.7101, 0.2, 0.4)));
        Assert.Equal('_', UnderscoreHyphenRule.Verdict(new BarMeasurement(1.05, 0.45, 0.6)));
        Assert.Null(UnderscoreHyphenRule.Verdict(new BarMeasurement(1.05, 0.4501, 0.6)));
    }

    [Fact]
    public void Measure_RowsWithLessThanAQuarterOfTheBusiestRowAreSpecks_ExactlyAQuarterCounts()
    {
        // the bar: rows 96 and 97, 20 columns each (busiest row 20, limit 5). A speck of 4 pixels in row 60 is ignored ...
        var withSmallSpeck = Mask(30, 130, (96, 97, 4, 24), (60, 60, 4, 8));
        var ignored = UnderscoreHyphenRule.Measure(withSmallSpeck, 0, 30, Band)!.Value;
        Assert.Equal(0.87, ignored.Frac, 12);
        Assert.Equal(0.02, ignored.Thick, 12);

        // ... one of exactly 5 pixels (a quarter) belongs to the bar: rows 60 to 97, centre 79, frac 0.69, thick 0.38
        var withQuarterSpeck = Mask(30, 130, (96, 97, 4, 24), (60, 60, 4, 9));
        var included = UnderscoreHyphenRule.Measure(withQuarterSpeck, 0, 30, Band)!.Value;
        Assert.Equal(0.69, included.Frac, 12);
        Assert.Equal(0.38, included.Thick, 12);
        Assert.Equal('-', UnderscoreHyphenRule.Verdict(included));
    }

    [Fact]
    public void Measure_OnlyTheColumnsOfTheGlyphCount_AndNothingToMeasureGivesNull()
    {
        // a bar at columns 4 to 23 and a tall letter at columns 40 to 43: the glyph [4, 24) is the bar alone
        var mask = Mask(50, 130, (96, 97, 4, 24), (10, 109, 40, 44));

        var bar = UnderscoreHyphenRule.Measure(mask, 4, 24, Band)!.Value;
        Assert.Equal(0.87, bar.Frac, 12);

        Assert.Null(UnderscoreHyphenRule.Measure(mask, 26, 38, Band));  // no ink in these columns
        Assert.Null(UnderscoreHyphenRule.Measure(mask, 30, 30, Band));  // an empty span
    }

    // ---- the correction of a read: Correct(PieceRead, text) ----

    /// <summary>A line made of a capital I (columns 0 to 3, rows 10 to 109: the capital band) and a bar (columns 6 to 25) at <paramref name="row0"/>, two rows high.</summary>
    private static PieceRead LineWithBar(int row0)
    {
        var mask = Mask(30, 130, (10, 109, 0, 4), (row0, row0 + 1, 6, 26));
        return new PieceRead(Array.Empty<ReadPiece>(), 100, new[] { (0, 4), (6, 26) }, mask);
    }

    [Theory]
    [InlineData("I-", 96, "I_")]   // read as a hyphen, sits at the bottom: an underscore (the misread of the test character)
    [InlineData("I_", 56, "I-")]   // read as an underscore, sits in the middle: a hyphen
    [InlineData("I-", 56, "I-")]   // right already
    [InlineData("I_", 100, "I_")]
    [InlineData("I-", 90, "I-")]   // 0.81: undecided, the model's character stays
    [InlineData("I_", 90, "I_")]
    public void Correct_ReplacesTheBarByTheVerdict(string text, int row0, string expected) =>
        Assert.Equal(expected, UnderscoreHyphenRule.Correct(LineWithBar(row0), text));

    [Fact]
    public void Correct_ChangesOnlyBars_EveryOtherCharacterStays()
    {
        // I, bar, I, bar: the letters are read as O and 0 here on purpose, and must not be touched
        var mask = Mask(60, 130, (10, 109, 0, 4), (96, 97, 6, 26), (10, 109, 28, 32), (56, 57, 34, 54));
        var read = new PieceRead(Array.Empty<ReadPiece>(), 100, new[] { (0, 4), (6, 26), (28, 32), (34, 54) }, mask);

        Assert.Equal("O_0-", UnderscoreHyphenRule.Correct(read, "O-0_")); // both bars swapped by their height
    }

    [Fact]
    public void Correct_TheBandIsTheLinesOwn_EveryRowOfAMultiRowNameHasItsOwn()
    {
        // a smaller line: capitals on rows 5 to 54 (cap 50), a bar on rows 51 and 52: centre 52, (52 - 5) / 50 = 0.94: an underscore.
        // With the band of the 100 row line above (top 10, cap 100) the same rows would give (52 - 10) / 100 = 0.42: a hyphen.
        var mask = Mask(30, 70, (5, 54, 0, 4), (51, 52, 6, 26));
        var read = new PieceRead(Array.Empty<ReadPiece>(), 50, new[] { (0, 4), (6, 26) }, mask);

        Assert.Equal("I_", UnderscoreHyphenRule.Correct(read, "I-"));
        var measured = UnderscoreHyphenRule.Measure(mask, 6, 26, Band)!.Value;
        Assert.Equal(0.42, measured.Frac, 12);
    }

    [Fact]
    public void Correct_LeavesTheLineAsRead_WhenTheSpansDoNotMatchTheTextOneToOne()
    {
        var read = LineWithBar(96);

        Assert.Equal("I--", UnderscoreHyphenRule.Correct(read, "I--"));     // 3 characters, 2 spans: a cut failed
        Assert.Equal("I-", UnderscoreHyphenRule.Correct(new PieceRead(Array.Empty<ReadPiece>(), 100), "I-")); // no spans recorded
        Assert.Equal("I-", UnderscoreHyphenRule.Correct(new PieceRead(Array.Empty<ReadPiece>(), 100, new[] { (0, 4), (6, 26) }), "I-")); // spans but no mask
    }

    [Fact]
    public void Correct_AReadWithoutBars_IsReturnedAsIs()
    {
        var read = LineWithBar(96);

        const string text = "IO";
        Assert.Same(text, UnderscoreHyphenRule.Correct(read, text)); // no allocation, the very same string
    }

    [Fact]
    public void Correct_ABarWithNoInkInItsColumns_KeepsTheModelsCharacter()
    {
        var mask = Mask(40, 130, (10, 109, 0, 4));
        var read = new PieceRead(Array.Empty<ReadPiece>(), 100, new[] { (0, 4), (20, 30) }, mask); // the second span holds no ink

        Assert.Equal("I-", UnderscoreHyphenRule.Correct(read, "I-"));
    }
}

/// <summary>
/// LOCAL ONLY parity of the C# rule with the Python reference on REAL line crops: the folder named by the environment variable <c>IRIS_US_FIXTURES</c> (the training kit's
/// <c>fixtures-us</c>) holds <c>caseNN.png</c> (a crop of a screen with one nameplate line, and <c>line_box</c> = x, y, width, height of that line in it) and <c>caseNN.json</c> (what the
/// reference found: the read text, the cap band and, for every bar glyph of the line, its index, the model's character, frac, thick and the verdict). The crops come from game footage (and
/// the test character's own name) and are never committed; without the variable the test is skipped.
/// For every case the reader is run on the PNG with that box and must give the reference's text and cap band; then for every bar: the glyph's mask columns (<c>mask_cols</c>) and the bar's
/// rows (<c>ink_rows</c>) exactly, frac, thick and length within 1e-9, and the same verdict (null = no verdict); the corrected text must be the reference's <c>text_after</c>.
/// </summary>
public class UnderscoreHyphenLocalParityTests
{
    private const double Tolerance = 1e-9;

    [LocalUsFixtureFact]
    public void FracAndVerdict_MatchPythonOnTheRealLines()
    {
        var folder = LocalUsFixtureFactAttribute.Folder!;
        var cases = Directory.GetFiles(folder, "case*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(cases);
        var reader = new NameplateReader(LetterModel.LoadEmbedded());
        var alphabet = reader.CreateAlphabet(NameplateDetector.NameAlphabet);
        var problems = new List<string>();
        var bars = 0;
        foreach (var jsonPath in cases)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var e = doc.RootElement;
            var name = e.GetProperty("case").GetString()!;
            using var image = Cv2.ImRead(Path.ChangeExtension(jsonPath, ".png"), ImreadModes.Color);
            if (image.Empty())
            {
                problems.Add($"{name}: cannot read the PNG");
                continue;
            }

            var lineBox = e.GetProperty("line_box");
            var box = new Rect(lineBox[0].GetInt32(), lineBox[1].GetInt32(), lineBox[2].GetInt32(), lineBox[3].GetInt32());
            var mask = NameplateFeatures.CreateMask(image, box, pad: e.GetProperty("pad").GetInt32());
            if (mask is null)
            {
                problems.Add($"{name}: no mask");
                continue;
            }

            var read = reader.ReadPieces(mask, alphabet);
            var text = string.Concat(read.Pieces.Select(p => p.Text));
            var expectedText = e.GetProperty("model_text").GetString()!;
            if (text != expectedText)
            {
                problems.Add($"{name}: read '{text}', Python '{expectedText}'");
                continue;
            }

            if (read.CharacterSpans is null || read.CharacterSpans.Count != text.Length)
            {
                problems.Add($"{name}: {read.CharacterSpans?.Count} spans for {text.Length} characters");
                continue;
            }

            var band = NameplateFeatures.FindCapBand(mask);
            var expectedBand = e.GetProperty("cap_band");
            if (band.Top != expectedBand[0].GetInt32() || band.Bottom != expectedBand[1].GetInt32())
                problems.Add($"{name}: cap band ({band.Top}, {band.Bottom}), Python ({expectedBand[0].GetInt32()}, {expectedBand[1].GetInt32()})");

            foreach (var bar in e.GetProperty("bars").EnumerateArray())
            {
                bars++;
                var index = bar.GetProperty("index").GetInt32();
                var label = $"{name} bar {index}";
                if (text[index] != bar.GetProperty("model_char").GetString()![0])
                {
                    problems.Add($"{label}: the read has '{text[index]}' there, Python '{bar.GetProperty("model_char").GetString()}'");
                    continue;
                }

                var (start, end) = read.CharacterSpans[index];
                if (UnderscoreHyphenRule.Measure(mask, start, end, band) is not { } measured)
                {
                    problems.Add($"{label}: nothing to measure");
                    continue;
                }

                var cols = bar.GetProperty("mask_cols");
                if (start != cols[0].GetInt32() || end != cols[1].GetInt32())
                    problems.Add($"{label}: glyph columns [{start}, {end}), Python [{cols[0].GetInt32()}, {cols[1].GetInt32()})");
                var rows = bar.GetProperty("ink_rows");
                if (measured.FirstRow != rows[0].GetInt32() || measured.LastRow != rows[1].GetInt32())
                    problems.Add($"{label}: bar rows {measured.FirstRow}..{measured.LastRow}, Python {rows[0].GetInt32()}..{rows[1].GetInt32()}");
                foreach (var (what, got) in new[] { ("frac", measured.Frac), ("thick", measured.Thick), ("length", measured.Length) })
                {
                    var want = bar.GetProperty(what).GetDouble();
                    if (Math.Abs(got - want) > Tolerance)
                        problems.Add($"{label}: {what} {got:F12}, Python {want:F12}");
                }

                var expectedVerdict = bar.GetProperty("verdict").ValueKind == JsonValueKind.Null ? (char?)null : bar.GetProperty("verdict").GetString()![0];
                if (UnderscoreHyphenRule.Verdict(measured) != expectedVerdict)
                    problems.Add($"{label}: verdict '{UnderscoreHyphenRule.Verdict(measured)}' (frac {measured.Frac:F4}, thick {measured.Thick:F4}), Python '{expectedVerdict}'");
            }

            if (e.TryGetProperty("text_after", out var after) && after.ValueKind == JsonValueKind.String
                && UnderscoreHyphenRule.Correct(read, text) != after.GetString())
                problems.Add($"{name}: corrected '{UnderscoreHyphenRule.Correct(read, text)}', Python '{after.GetString()}'");
        }

        Assert.True(bars > 0, "no bar glyph was compared");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
