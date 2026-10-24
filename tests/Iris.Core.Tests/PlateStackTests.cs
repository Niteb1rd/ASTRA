using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// PARITY WITH PYTHON for multi-row names and the plate colour. The frames are synthetic (made-up words in a Hershey font, see Fixtures/plates/make_plate_fixtures.py):
/// plates_a has a 3-row name, a 2-row name (a full first row and a short second row), a single row plate and a white 2-row plate; plates_b has the cases that must NOT join
/// (a row 30 px off center, a middle row off center, a junk line of two words above a name, a name-like row far off center above a short plate, and four rows); plates_c has
/// the colours (BLUE, GREEN, RED, pure white, light cyan; single and multi row). The expected values come from detector_ref.py with the o0 rule on.
/// Boxes may differ by a pixel; names, rows, the colour class and the joins must be equal.
/// </summary>
public class PlateStackTests
{
    private const int BoxTolerance = 1;

    private static readonly LetterModel Model = LetterModel.LoadEmbedded();

    private static bool Near(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= BoxTolerance && Math.Abs(a.Y - b.Y) <= BoxTolerance
        && Math.Abs(a.Width - b.Width) <= BoxTolerance && Math.Abs(a.Height - b.Height) <= BoxTolerance;

    private static PlateFixtureFrame Frame(string file) => PlateFixtures.Load().Single(f => f.File == file);

    [Fact]
    public void Fixtures_AreThere()
    {
        var frames = PlateFixtures.Load();

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.True(File.Exists(f.ImagePath), f.ImagePath));
        Assert.Contains(frames.SelectMany(f => f.Detections), d => d.Rows.Length == 3);
        Assert.Contains(frames.SelectMany(f => f.Detections), d => d.Rows.Length == 2);
        Assert.Contains(frames.SelectMany(f => f.Detections), d => d.Rows.Length == 0);
        Assert.Contains(frames.SelectMany(f => f.Evaluations), e => e.PairIsNameplate && e.Name is null); // a partial stack: a nameplate without a name
    }

    /// <summary>The numbers of <see cref="NameplateDetectorSettings.Default"/> are the numbers the reference used.</summary>
    [Fact]
    public void DefaultSettings_AreTheOnesThePythonReferenceUsed()
    {
        PlateFixtures.Load();
        var p = PlateFixtures.Parameters;
        var s = NameplateDetectorSettings.Default;

        Assert.Equal(s.MaxRows, p.GetProperty("max_rows").GetInt32());
        Assert.Equal(s.RowGapMax, p.GetProperty("row_gap_max").GetDouble());
        Assert.Equal(s.RowPitchMin, p.GetProperty("row_pitch_min").GetDouble());
        Assert.Equal(s.RowPitchMax, p.GetProperty("row_pitch_max").GetDouble());
        Assert.Equal(s.RowMinCaps, p.GetProperty("row_min_caps").GetDouble());
        Assert.Equal(s.CenterTolerance, p.GetProperty("center_tol").GetDouble());
        Assert.Equal(s.MinNameLength, p.GetProperty("name_min").GetInt32());
        Assert.Equal(s.MaxNameLength, p.GetProperty("name_max").GetInt32());
        Assert.Equal(s.SubPixelO0Rule, p.GetProperty("o0_rule").GetBoolean());
        var c = s.PlateColorRules;
        Assert.Equal(c.WhiteSaturation, p.GetProperty("white_sat").GetDouble());
        Assert.Equal(c.GreenSaturation, p.GetProperty("green_sat").GetDouble());
        Assert.Equal(c.BlueSaturation, p.GetProperty("blue_sat").GetDouble());
        Assert.Equal(c.GreenHueFrom, p.GetProperty("green_hue")[0].GetDouble());
        Assert.Equal(c.GreenHueTo, p.GetProperty("green_hue")[1].GetDouble());
        Assert.Equal(c.RedHueFrom, p.GetProperty("red_hue")[0].GetDouble());
        Assert.Equal(c.RedHueTo, p.GetProperty("red_hue")[1].GetDouble());
        Assert.Equal(c.BlueHueFrom, p.GetProperty("blue_hue")[0].GetDouble());
        Assert.Equal(c.BlueHueTo, p.GetProperty("blue_hue")[1].GetDouble());
    }

    [Fact]
    public void Settings_RejectNonsenseRowLimits()
    {
        Assert.NotNull((NameplateDetectorSettings.Default with { MaxRows = 0 }).Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { RowPitchMin = 2.0, RowPitchMax = 1.0 }).Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { RowGapMax = -1 }).Validate());
    }

    /// <summary>The rows stacked above every pair, and the blockers, equal the reference.</summary>
    [OpenCvFact]
    public void Stacks_MatchPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var frame in PlateFixtures.Load())
        {
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            using var detector = new NameplateDetector(reader);
            var candidates = detector.FindCandidates(image);
            Assert.Equal(frame.Boxes.Length, candidates.Boxes.Count);
            var actual = candidates.Stacks ?? new Dictionary<(Rect Top, Rect Bottom), PlateStack>();
            if (actual.Count != frame.Stacks.Length)
                problems.Add($"{frame.File}: {actual.Count} stacks, Python {frame.Stacks.Length}");
            foreach (var expected in frame.Stacks)
            {
                var found = actual.FirstOrDefault(kv => Near(kv.Key.Top, expected.Top) && Near(kv.Key.Bottom, expected.Bottom));
                if (found.Value is null)
                {
                    problems.Add($"{frame.File}: no stack at {expected.Top}");
                    continue;
                }

                if (found.Value.Rows.Count != expected.Rows.Length || !found.Value.Rows.Zip(expected.Rows, (a, b) => Near(a, b)).All(x => x))
                    problems.Add($"{frame.File}: rows at {expected.Top} differ: {string.Join(" ", found.Value.Rows)} vs Python {string.Join(" ", expected.Rows)}");
                if (found.Value.Blockers.Count != expected.Blockers.Length || !found.Value.Blockers.Zip(expected.Blockers, (a, b) => Near(a, b)).All(x => x))
                    problems.Add($"{frame.File}: blockers at {expected.Top} differ");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The detections (names, rows, plate box, colour class and the numbers it came from) equal the reference.</summary>
    [OpenCvFact]
    public void Detections_MatchPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var frame in PlateFixtures.Load())
        {
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            using var detector = new NameplateDetector(reader);
            var actual = detector.Detect(image);
            if (actual.Count != frame.Detections.Length)
                problems.Add($"{frame.File}: {actual.Count} detections ({string.Join(", ", actual.Select(a => a.Name))}), Python {frame.Detections.Length}");
            foreach (var expected in frame.Detections)
            {
                var found = actual.FirstOrDefault(a => a.Name == expected.Name);
                if (found is null)
                {
                    problems.Add($"{frame.File}: '{expected.Name}' not found");
                    continue;
                }

                if (!Near(found.NameBox, expected.NameBox) || !Near(found.DistanceBox, expected.DistanceBox) || found.DistanceText != expected.DistanceText)
                    problems.Add($"{frame.File}: {expected.Name}: boxes or distance differ");
                var expectedRows = expected.Rows.Length > 1 ? expected.Rows : new[] { expected.NameBox };
                if (found.NameRows.Count != expectedRows.Length || !found.NameRows.Zip(expectedRows, (a, b) => Near(a, b)).All(x => x))
                    problems.Add($"{frame.File}: {expected.Name}: rows {string.Join(" ", found.NameRows)} vs Python {string.Join(" ", expectedRows)}");
                if (found.IsMultiRow != expected.Rows.Length > 1)
                    problems.Add($"{frame.File}: {expected.Name}: IsMultiRow {found.IsMultiRow}");
                var union = PlateBoxes.PlateBox(expectedRows, expected.DistanceBox);
                if (!Near(found.PlateBox, union))
                    problems.Add($"{frame.File}: {expected.Name}: plate box {found.PlateBox} vs {union}");
                if (found.Color?.ToString().ToUpperInvariant() != expected.Color)
                    problems.Add($"{frame.File}: {expected.Name}: colour {found.Color}, Python {expected.Color}");
                if (expected.Saturation >= 0.15 && Math.Abs(found.Hue - expected.Hue) > 1.5)
                    problems.Add($"{frame.File}: {expected.Name}: hue {found.Hue:F1}, Python {expected.Hue:F1}");
                if (Math.Abs(found.Saturation - expected.Saturation) > 0.02)
                    problems.Add($"{frame.File}: {expected.Name}: saturation {found.Saturation:F3}, Python {expected.Saturation:F3}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>Every candidate pair evaluates as in the reference, with the stack the cheap pass found for it.</summary>
    [OpenCvFact]
    public void Evaluations_MatchPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var frame in PlateFixtures.Load())
        {
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            using var detector = new NameplateDetector(reader);
            var candidates = detector.FindCandidates(image);
            var evaluator = detector.CreateEvaluator(image, candidates);
            var pairs = candidates.Pairs.Concat(candidates.CrowdPairs.Select(c => (c.Top, c.Bottom))).Concat(candidates.LoosePairs).ToList();
            Assert.Equal(frame.Evaluations.Length, pairs.Count);
            foreach (var (top, bottom) in pairs)
            {
                var topBox = candidates.Boxes[top];
                var bottomBox = candidates.Boxes[bottom];
                var reference = frame.Evaluations.FirstOrDefault(e => Near(e.Top, topBox) && Near(e.Bottom, bottomBox));
                if (reference is null)
                {
                    problems.Add($"{frame.File}: no evaluation in Python for {topBox}");
                    continue;
                }

                var alone = detector.Evaluate(image, topBox, bottomBox, candidates.StackOf(topBox, bottomBox));
                var actual = evaluator.Evaluate(topBox, bottomBox);
                if (alone.IsNameplate != reference.PairIsNameplate)
                    problems.Add($"{frame.File}: {topBox} pair alone nameplate={alone.IsNameplate}, Python {reference.PairIsNameplate}");
                else if (actual.IsNameplate != reference.IsNameplate)
                    problems.Add($"{frame.File}: {topBox} nameplate={actual.IsNameplate}, Python {reference.IsNameplate}");
                else if (actual.Detection?.Name != reference.Name)
                    problems.Add($"{frame.File}: {topBox} name '{actual.Detection?.Name}', Python '{reference.Name}'");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [OpenCvFact]
    public void GoodPlates_JoinTheirRows()
    {
        var frame = Frame("plates_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model));

        var found = detector.Detect(image).ToDictionary(d => d.Name);

        var three = found["STACKEDHANDLEPLATEMIXEDFIXTUREHANDLESAMPLEEND"];
        Assert.Equal(3, three.NameRows.Count);
        Assert.Equal(three.NameBox, three.NameRows[three.NameRows.Count - 1]);
        var two = found["UPPERLAYERHANDLETAILPIECE"];
        Assert.Equal(2, two.NameRows.Count);
        Assert.True(two.NameRows[0].Width > 2 * two.NameRows[1].Width * 0.9); // a full first row, a short second row
        var single = found["SINGLEROWPLATE"];
        Assert.False(single.IsMultiRow);
        Assert.Equal(single.NameBox, Assert.Single(single.NameRows));
        // the plate box holds every row and the distance line
        Assert.True(three.PlateBox.Y <= three.NameRows[0].Y && three.PlateBox.Bottom >= three.DistanceBox.Bottom);
        Assert.True(three.PlateBox.Width >= three.NameRows.Max(r => r.Width));
        Assert.Equal(PlateColor.White, three.Color);
    }

    [OpenCvFact]
    public void PlatesThatMustNotJoin_GiveNoNameOrOnlyTheNameLine()
    {
        var frame = Frame("plates_b.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model));

        var found = detector.Detect(image);

        // the junk line of two words is not joined: the name line alone is read, as on a single row plate
        var only = Assert.Single(found);
        Assert.Equal("SAMPLEEND", only.Name);
        Assert.False(only.IsMultiRow);
        // the off center rows and the fourth row make nameplates without a name: the evaluation says "nameplate", but gives no vote
        var candidates = detector.FindCandidates(image);
        var partial = candidates.Pairs
            .Select(p => (Top: candidates.Boxes[p.Top], Bottom: candidates.Boxes[p.Bottom]))
            .Select(p => detector.Evaluate(image, p.Top, p.Bottom, candidates.StackOf(p.Top, p.Bottom)))
            .Count(e => e.IsNameplate && e.Detection is null);
        Assert.Equal(4, partial);
    }

    [OpenCvFact]
    public void PlateColors_AreClassifiedLikePython()
    {
        var frame = Frame("plates_c.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model));

        var found = detector.Detect(image).ToDictionary(d => d.Name);

        Assert.Equal(PlateColor.Blue, found["BLUEPLATESAMPLE"].Color);
        Assert.Equal(PlateColor.Blue, found["UPPERLAYERHANDLETAILPIECE"].Color);
        Assert.Equal(PlateColor.Green, found["GREENPLATESAMPLE"].Color);
        Assert.Equal(PlateColor.Red, found["REDPLATESAMPLE"].Color);
        Assert.Equal(PlateColor.Red, found["STACKEDHANDLEPLATEMIXEDFIXTUREHANDLESAMPLEEND"].Color);
        Assert.Equal(PlateColor.White, found["PUREWHITESAMPLE"].Color);
        Assert.Equal(PlateColor.White, found["LIGHTCYANSAMPLE"].Color); // light cyan counts as white
        Assert.InRange(found["BLUEPLATESAMPLE"].Hue, 195, 270);
    }

    /// <summary>The reader records the mask columns of every character: one span per character, so the O/0 rule can cut each glyph out.</summary>
    [OpenCvFact]
    public void Reader_RecordsOneSpanPerCharacter()
    {
        var frame = Frame("plates_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        var reader = new NameplateReader(Model);
        var alphabet = reader.CreateAlphabet(NameplateDetector.NameAlphabet);
        foreach (var detection in frame.Detections)
        {
            var read = reader.ReadPieces(image, detection.NameBox, alphabet)!;
            var text = string.Concat(read.Pieces.Select(p => p.Text));
            Assert.Equal(text.Length, read.CharacterSpans!.Count);
            Assert.NotNull(read.Mask);
            Assert.All(read.CharacterSpans, s => Assert.InRange(s.End, s.Start + 1, read.Mask!.Width));
        }
    }
}
