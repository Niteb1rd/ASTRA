using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// PARITY WITH PYTHON for the nameplate detector. The frames are synthetic (made-up names drawn with a Hershey font on generated
/// backgrounds, plus decoys: single lines, a speed line, a name over a solid bar, a HUD word pair, a left aligned label column,
/// a distance without a decimal, two words, a neighbor too close, a reticle ring, a nameplate outside the scan region, frames of two
/// sizes, one with a bright sky and one with a crowd: nameplates side by side, a plate beside a HUD word pair, a plate beside a ring); the expected values come from the Python reference (detector_ref.py v2 with OpenCV 5.0 in
/// Python, OpenCvSharp 4.11 here). Boxes may differ by a pixel if another OpenCV build rounds differently; names and the text read
/// from the distance line must be equal.
/// </summary>
public class NameplateDetectorTests
{
    private const int BoxTolerance = 1;
    private const double ScoreTolerance = 0.1;

    private static readonly LetterModel Model = LetterModel.LoadEmbedded();

    private static NameplateDetectorSettings WholeFrame { get; } = NameplateDetectorSettings.Default with
    {
        RegionLeft = 0.0,
        RegionRight = 1.0,
        RegionTop = 0.0,
        RegionBottom = 1.0,
    };

    [Fact]
    public void Fixtures_AreThere()
    {
        var frames = DetectorFixtures.Load();

        Assert.Equal(6, frames.Count);
        Assert.All(frames, f => Assert.True(File.Exists(f.ImagePath), f.ImagePath));
        Assert.Contains(frames, f => f.Default.Detections.Length == 0);
        Assert.Contains(frames, f => f.Height != 1440); // a frame that needs the scaled constants
        Assert.Contains(frames, f => f.WholeFrame.LoosePairs.Length > 0); // a pair that fails only the isolation test
        Assert.Contains(frames, f => f.WholeFrame.CrowdPairs.Length > 0); // a pair that is isolated but for the text of other pairs
    }

    /// <summary>The numbers the reference used to make the fixtures are the numbers of <see cref="NameplateDetectorSettings.Default"/>.</summary>
    [Fact]
    public void DefaultSettings_AreTheOnesThePythonReferenceUsed()
    {
        DetectorFixtures.Load();
        var p = DetectorFixtures.Parameters;
        var s = NameplateDetectorSettings.Default;

        Assert.Equal(s.RegionLeft, p.GetProperty("region")[0].GetDouble());
        Assert.Equal(s.RegionRight, p.GetProperty("region")[1].GetDouble());
        Assert.Equal(s.RegionTop, p.GetProperty("region")[2].GetDouble());
        Assert.Equal(s.RegionBottom, p.GetProperty("region")[3].GetDouble());
        Assert.Equal(s.ReferenceHeight, p.GetProperty("ref_height").GetInt32());
        Assert.Equal(s.MinNameLength, p.GetProperty("name_min").GetInt32());
        Assert.Equal(s.MaxNameLength, p.GetProperty("name_max").GetInt32());
        Assert.Equal(s.MinNameScore, p.GetProperty("min_name_score").GetDouble());
        Assert.Equal(s.MinDistanceScore, p.GetProperty("min_dist_score").GetDouble());
        Assert.Equal(s.MaxDistanceGap, p.GetProperty("max_dist_gap").GetDouble());
        Assert.Equal(s.MaxDistanceValue, p.GetProperty("max_dist_value").GetInt32());
        Assert.Equal(s.DistanceEndScore, p.GetProperty("dist_end_score").GetDouble());
        Assert.Equal(s.WordGap, p.GetProperty("word_gap").GetDouble());
        Assert.Equal(s.ClearLines, p.GetProperty("clear_caps").GetDouble());
        Assert.Equal(s.SpeckArea, p.GetProperty("speck_area").GetDouble());
        Assert.Equal(s.CenterTolerance, p.GetProperty("center_tol").GetDouble());
        Assert.Equal(s.LeftTolerance, p.GetProperty("left_tol").GetDouble());
        Assert.Equal(s.LeftWidthDifference, p.GetProperty("left_width_diff").GetDouble());
        Assert.Equal(s.DistanceMinInk, p.GetProperty("dist_min_ink").GetDouble());
        Assert.Equal(s.CrowdException, p.GetProperty("crowd_exception").GetBoolean());
        Assert.Equal(s.SpeedJoinGap, p.GetProperty("speed_join_gap").GetDouble());
        Assert.Equal(s.SpeedJoinMaxLines, p.GetProperty("speed_join_max_lines").GetDouble());
        Assert.Equal(s.BracketMinScore, p.GetProperty("bracket_min_score").GetDouble());
    }

    [Fact]
    public void Scale_FollowsTheFrameHeight()
    {
        var s = NameplateDetectorSettings.Default;

        Assert.Equal(11, s.Scale(1440, 11));   // the reference height changes nothing
        Assert.Equal(22, s.Scale(1440, 22));
        Assert.Equal(5, s.Scale(1080, 7));     // 5.25
        Assert.Equal(17, s.Scale(1080, 22));   // 16.5 rounds up
        Assert.Equal(17, s.Scale(2160, 11));   // 4K: 16.5 rounds up
        Assert.Equal(1, s.Scale(100, 1));      // never below 1
    }

    [Fact]
    public void LongestHandle_FitsTheLineWidthFilter()
    {
        Assert.Equal(64, NameplateDetectorSettings.Default.MaxNameLength);
        Assert.Equal(Iris.Core.Rsi.RsiNames.MaxHandleLength, NameplateDetectorSettings.Default.MaxNameLength);
        // a letter advances 10.1 px on average and 11.7 px at most (measured at 1440 high): 64 letters must fit with margin
        Assert.True(NameplateDetector.MaxLineWidth >= 64 * 11.7 * 1.1);
        Assert.Equal(832, NameplateDetectorSettings.Default.Scale(1440, NameplateDetector.MaxLineWidth));
        Assert.Equal(1248, NameplateDetectorSettings.Default.Scale(2160, NameplateDetector.MaxLineWidth));
    }

    [Fact]
    public void Settings_RejectNonsense()
    {
        Assert.Null(NameplateDetectorSettings.Default.Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { RegionLeft = 0.9, RegionRight = 0.1 }).Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { RegionBottom = 1.5 }).Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { MinNameLength = 10, MaxNameLength = 4 }).Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { MaxCandidateTopsPerFrame = 0 }).Validate());
        Assert.NotNull((NameplateDetectorSettings.Default with { SpeedJoinGap = -1 }).Validate());
    }

    /// <summary>Text lines and pairs (stages 2 to 4: mask, lines, pairing) equal the reference, with the default scan region and with the whole frame.</summary>
    [OpenCvFact]
    public void Candidates_MatchPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var frame in DetectorFixtures.Load())
        {
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            Assert.Equal(frame.Height, image.Height);
            Assert.Equal(frame.Width, image.Width);
            foreach (var (label, settings, expected) in Scans(frame))
            {
                using var detector = new NameplateDetector(reader, settings);
                var actual = detector.FindCandidates(image);
                CompareCandidates($"{frame.File} {label}", expected, actual, problems);
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The whole path: the right nameplates, the right names, nothing from the decoys.</summary>
    [OpenCvFact]
    public void Detections_MatchPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var frame in DetectorFixtures.Load())
        {
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            foreach (var (label, settings, expected) in Scans(frame))
            {
                using var detector = new NameplateDetector(reader, settings);
                CompareDetections($"{frame.File} {label}", expected.Detections, detector.Detect(image), problems);
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// The evaluation of every candidate pair (strict, crowd and loose) equals the reference: its kind, whether the pair alone is a nameplate,
    /// whether it is one for the frame evaluator (a crowd pair needs real nameplates beside it), and which name it read.
    /// </summary>
    [OpenCvFact]
    public void Evaluations_MatchPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var frame in DetectorFixtures.Load())
        {
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            foreach (var (label, settings, expected) in Scans(frame))
            {
                using var detector = new NameplateDetector(reader, settings);
                var candidates = detector.FindCandidates(image);
                var evaluator = detector.CreateEvaluator(image, candidates);
                var pairs = candidates.Pairs.Select(p => (p.Top, p.Bottom, Kind: "strict"))
                    .Concat(candidates.CrowdPairs.Select(p => (p.Top, p.Bottom, Kind: "crowd")))
                    .Concat(candidates.LoosePairs.Select(p => (p.Top, p.Bottom, Kind: "loose")))
                    .ToList();
                if (pairs.Count != expected.Evaluations.Length)
                {
                    problems.Add($"{frame.File} {label}: {pairs.Count} pairs to evaluate, Python {expected.Evaluations.Length}");
                    continue;
                }

                foreach (var (top, bottom, kind) in pairs)
                {
                    var topBox = candidates.Boxes[top];
                    var bottomBox = candidates.Boxes[bottom];
                    var reference = expected.Evaluations.FirstOrDefault(e => Near(e.Top, topBox) && Near(e.Bottom, bottomBox));
                    if (reference is null)
                    {
                        problems.Add($"{frame.File} {label}: no evaluation in Python for {topBox} / {bottomBox}");
                        continue;
                    }

                    var alone = detector.Evaluate(image, topBox, bottomBox);
                    var actual = evaluator.Evaluate(topBox, bottomBox);
                    if (reference.Kind != kind)
                        problems.Add($"{frame.File} {label}: {topBox} is {kind}, Python {reference.Kind}");
                    else if (reference.PairIsNameplate != alone.IsNameplate)
                        problems.Add($"{frame.File} {label}: {topBox} pair alone nameplate={alone.IsNameplate}, Python {reference.PairIsNameplate}");
                    else if (reference.IsNameplate != actual.IsNameplate)
                        problems.Add($"{frame.File} {label}: {topBox} nameplate={actual.IsNameplate}, Python {reference.IsNameplate}");
                    else if (reference.Name != actual.Detection?.Name)
                        problems.Add($"{frame.File} {label}: {topBox} name '{actual.Detection?.Name}', Python '{reference.Name}'");
                    else if (reference.Name is not null && reference.DistanceText != actual.Detection!.DistanceText)
                        problems.Add($"{frame.File} {label}: {topBox} distance read '{actual.Detection.DistanceText}', Python '{reference.DistanceText}'");
                    else if (reference.Name is not null && reference.SpeedGroup != actual.Detection!.HasSpeedGroup)
                        problems.Add($"{frame.File} {label}: {topBox} speed group {actual.Detection.HasSpeedGroup}, Python {reference.SpeedGroup}");
                    else if (reference.Name is not null && Math.Abs(reference.Score - actual.Detection!.Score) > ScoreTolerance)
                        problems.Add($"{frame.File} {label}: {topBox} score {actual.Detection.Score}, Python {reference.Score}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The nameplate outside the default scan region is found only when the region is the whole frame.</summary>
    [OpenCvFact]
    public void ScanRegion_DecidesWhatIsSeen()
    {
        var reader = new NameplateReader(Model);
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);

        using var normal = new NameplateDetector(reader);
        using var whole = new NameplateDetector(reader, WholeFrame);

        Assert.DoesNotContain(normal.Detect(image), d => d.Name == "TESTPLATE1");
        Assert.Contains(whole.Detect(image), d => d.Name == "TESTPLATE1");
    }

    [OpenCvFact]
    public void DecoysAreNotReported()
    {
        var reader = new NameplateReader(Model);
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(reader, WholeFrame);

        var names = detector.Detect(image).Select(d => d.Name).ToList();

        // single line, speed line, name over a bar, HUD word pair, left aligned column, no decimal, two words, a neighbor too close
        foreach (var decoy in new[] { "NOTAREAL_ONE", "MOCKUPFOX", "PLACEHOLDER2", "DECOY", "THROTTLE", "NODECIMAL1", "TWO", "WORDS", "NEIGHBOR_ONE" })
            Assert.DoesNotContain(decoy, names);
        Assert.Contains("SYNTH_ALPHA", names);
        Assert.Contains("SYNTH_BRAVO", names);
        Assert.Contains("DUMMY_DELTA", names);
        Assert.Contains("FIXTURE_ECHO", names);
    }

    /// <summary>A label column ("THROTTLE" over "THROTTL", left edges equal, widths different) is no pair unless the left rule is switched off.</summary>
    [OpenCvFact]
    public void LeftAlignedColumn_IsRejectedByTheLeftRule()
    {
        var reader = new NameplateReader(Model);
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);

        bool HasColumnPair(NameplateDetectorSettings settings)
        {
            using var detector = new NameplateDetector(reader, settings);
            var candidates = detector.FindCandidates(image);
            return candidates.Pairs.Concat(candidates.LoosePairs).Any(p =>
                Near(candidates.Boxes[p.Top], new Rect(298, 704, 90, 13)) && Near(candidates.Boxes[p.Bottom], new Rect(298, 732, 80, 13)));
        }

        Assert.False(HasColumnPair(WholeFrame));
        Assert.True(HasColumnPair(WholeFrame with { LeftWidthDifference = 9999 }));
    }

    /// <summary>A nameplate with another text right beside it is not a strict pair (it may only continue a track), though it reads as a nameplate.</summary>
    [OpenCvFact]
    public void ACrowdedNameplate_IsALoosePair()
    {
        var reader = new NameplateReader(Model);
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(reader, WholeFrame);

        var candidates = detector.FindCandidates(image);

        var loose = Assert.Single(candidates.LoosePairs);
        Assert.DoesNotContain(loose, candidates.Pairs);
        var evaluation = detector.Evaluate(image, candidates.Boxes[loose.Top], candidates.Boxes[loose.Bottom]);
        Assert.True(evaluation.IsNameplate);
        Assert.Equal("NEIGHBOR_ONE", evaluation.Detection?.Name);
        Assert.DoesNotContain(detector.Detect(image), d => d.Name == "NEIGHBOR_ONE");
    }

    // ------------------------------------------------------------------------------------------------ speed readouts (frame_f)

    private static readonly string[] SpeedPlates = { "SYNTH_ALPHA", "SYNTH_BRAVO", "DUMMY_DELTA", "FIXTURE_ECHO", "MOCK_FOXTROT" };

    /// <summary>
    /// `9.0km [-29m/s]` under a name: the anchor accepts a `[` after one word gap following the unit and ignores everything from the bracket on.
    /// Three plates have the speed group joined into the distance line (one box), two have it as a separate box that the detector joins.
    /// </summary>
    [OpenCvFact]
    public void SpeedReadouts_AreAnchoredOnTheDistancePart()
    {
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_f.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model), WholeFrame);

        var found = detector.Detect(image).ToDictionary(d => d.Name);

        foreach (var name in SpeedPlates)
        {
            Assert.True(found.ContainsKey(name), $"{name} not found");
            Assert.True(found[name].HasSpeedGroup, $"{name} has no speed group");
        }

        Assert.Equal("9.0km", found["SYNTH_ALPHA"].DistanceText);
        Assert.Equal("4.4km", found["SYNTH_BRAVO"].DistanceText);
        Assert.Equal("1.9km", found["DUMMY_DELTA"].DistanceText);
        Assert.Equal("7.7km", found["FIXTURE_ECHO"].DistanceText);
        Assert.Equal("238.5m", found["MOCK_FOXTROT"].DistanceText);
        Assert.Equal(SpeedPlates.Length, found.Count);
    }

    /// <summary>
    /// The decoys: a gap after the unit but no bracket, a minus sign instead of the bracket, no decimal, a speed group without a distance,
    /// speed first, and a neighbor too close after the speed group.
    /// </summary>
    [OpenCvFact]
    public void SpeedReadoutDecoys_AreNotReported()
    {
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_f.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model), WholeFrame);

        var names = detector.Detect(image).Select(d => d.Name).ToList();

        foreach (var decoy in new[] { "NOBRACKET_ONE", "NOBRACKET_TWO", "NODECIMAL_TWO", "NODISTANCE_ONE", "SPEEDFIRST_ONE", "NEIGHBOR_TWO" })
            Assert.DoesNotContain(decoy, names);
    }

    /// <summary>
    /// A speed group that the line joiner left as its own box is offered as one line with the distance: the pair's lower box is as wide as both
    /// parts together and centered under the name (the whole line is centered, not its distance part).
    /// </summary>
    [OpenCvFact]
    public void SplitSpeedGroup_IsJoinedIntoOneCenteredLine()
    {
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_f.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model), WholeFrame);

        var candidates = detector.FindCandidates(image);
        var echo = detector.Detect(image).Single(d => d.Name == "FIXTURE_ECHO");

        // the box under the name is the union: it is wider than the lines the joiner made of its two parts
        var parts = candidates.Boxes.Where(b => b.Y > echo.NameBox.Y && b.Y < echo.NameBox.Y + 3 * echo.NameBox.Height
            && b.X >= echo.DistanceBox.X - 1 && b.X + b.Width <= echo.DistanceBox.X + echo.DistanceBox.Width + 1 && b != echo.DistanceBox).ToList();
        Assert.True(parts.Count >= 2, "the two separate boxes of the distance and the speed group");
        Assert.Equal(parts.Min(b => b.X), echo.DistanceBox.X);
        Assert.Equal(parts.Max(b => b.X + b.Width), echo.DistanceBox.X + echo.DistanceBox.Width);
        var nameCenter = echo.NameBox.X + echo.NameBox.Width / 2.0;
        var lineCenter = echo.DistanceBox.X + echo.DistanceBox.Width / 2.0;
        Assert.InRange(Math.Abs(nameCenter - lineCenter), 0, NameplateDetectorSettings.Default.CenterTolerance);
        // ... and the distance part alone is NOT centered under the name
        var distancePart = parts.OrderBy(b => b.X).First();
        Assert.True(Math.Abs(distancePart.X + distancePart.Width / 2.0 - nameCenter) > NameplateDetectorSettings.Default.CenterTolerance);
    }

    /// <summary>
    /// With the brackets switched off (a bracket score nobody reaches) the speed lines are not anchors. Every plate of frame_f has a speed group (the
    /// others are decoys), so no plate at all is found.
    /// </summary>
    [OpenCvFact]
    public void WithoutTheBracketTest_SpeedLinesAreNotAnchors()
    {
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_f.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model), WholeFrame with { BracketMinScore = 1.0 });

        Assert.Empty(detector.Detect(image));
    }

    // ------------------------------------------------------------------------------------------------ the crowd exception (frame_e)

    private static (Mat Image, NameplateDetector Detector, NameplateCandidates Candidates, FrameEvaluator Evaluator) CrowdFrame(NameplateDetectorSettings settings)
    {
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_e.png");
        var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        var detector = new NameplateDetector(new NameplateReader(Model), settings);
        var candidates = detector.FindCandidates(image);
        return (image, detector, candidates, detector.CreateEvaluator(image, candidates));
    }

    /// <summary>Two real nameplates side by side (each isolated except for the other's text) are both found; the lone plate too.</summary>
    [OpenCvFact]
    public void NameplatesSideBySide_AreBothFound()
    {
        var (image, detector, candidates, _) = CrowdFrame(WholeFrame);
        using (image)
        using (detector)
        {
            var names = detector.Detect(image).Select(d => d.Name).ToList();

            Assert.Contains("CROWD_LEFT", names);
            Assert.Contains("CROWD_RIGHT", names);
            Assert.Contains("LONE_PLATE", names);
            Assert.Equal(4, candidates.CrowdPairs.Count); // the two real plates, and the plate and HUD word pair of the next test
            Assert.All(candidates.CrowdPairs, c => Assert.Single(c.Neighbours)); // each has one line of text beside it
        }
    }

    /// <summary>
    /// A plate beside a HUD word pair (no distance anchor) is a crowd candidate, but the neighbour is no nameplate, so it is rejected
    /// although it reads as a nameplate on its own. A plate beside a ring is not a crowd candidate at all (a ring is no text line): it stays loose.
    /// </summary>
    [OpenCvFact]
    public void ClutterBesideALine_DoesNotMakeItACrowd()
    {
        var (image, detector, candidates, evaluator) = CrowdFrame(WholeFrame);
        using (image)
        using (detector)
        {
            var names = detector.Detect(image).Select(d => d.Name).ToList();
            Assert.DoesNotContain("BESIDE_HUD", names);
            Assert.DoesNotContain("HUDWORD1", names);
            Assert.DoesNotContain("RINGSIDE_ONE", names);

            var besideHud = candidates.CrowdPairs.Select(c => (candidates.Boxes[c.Top], candidates.Boxes[c.Bottom]))
                .Single(p => Near(p.Item1, new Rect(343, 454, 113, 14)));
            Assert.True(detector.Evaluate(image, besideHud.Item1, besideHud.Item2).IsNameplate); // on its own it is a nameplate ...
            Assert.False(evaluator.Evaluate(besideHud.Item1, besideHud.Item2).IsNameplate);     // ... but the crowd around it is not

            var ring = Assert.Single(candidates.LoosePairs);
            Assert.True(Near(candidates.Boxes[ring.Top], new Rect(336, 654, 128, 14)));
            Assert.True(detector.Evaluate(image, candidates.Boxes[ring.Top], candidates.Boxes[ring.Bottom]).IsNameplate);
        }
    }

    /// <summary>With the exception switched off the crowd pairs are loose pairs again (the v2 rule): nothing in the crowd starts a track.</summary>
    [OpenCvFact]
    public void CrowdException_CanBeSwitchedOff()
    {
        var (image, detector, candidates, _) = CrowdFrame(WholeFrame with { CrowdException = false });
        using (image)
        using (detector)
        {
            Assert.Empty(candidates.CrowdPairs);
            Assert.Equal(5, candidates.LoosePairs.Count); // 4 former crowd pairs and the ring
            var names = detector.Detect(image).Select(d => d.Name).ToList();
            Assert.Equal(new[] { "LONE_PLATE" }, names);
        }
    }

    /// <summary>The tracker driver sees crowd pairs as strict candidates (their evaluation checks the neighbours), loose pairs as non-strict.</summary>
    [OpenCvFact]
    public void TrackCandidates_ListStrictCrowdAndLoosePairs()
    {
        var (image, detector, candidates, evaluator) = CrowdFrame(WholeFrame);
        using (image)
        using (detector)
        {
            var list = evaluator.TrackCandidates();

            Assert.Equal(candidates.Pairs.Count + candidates.CrowdPairs.Count + candidates.LoosePairs.Count, list.Count);
            Assert.Equal(candidates.Pairs.Count + candidates.CrowdPairs.Count, list.Count(c => c.Strict));
            Assert.Equal(candidates.LoosePairs.Count, list.Count(c => !c.Strict));
        }
    }

    [OpenCvFact]
    public void Evaluate_RejectsWhatIsNotAPair()
    {
        var reader = new NameplateReader(Model);
        var frame = DetectorFixtures.Load().First(f => f.File == "frame_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(reader, WholeFrame);
        var candidates = detector.FindCandidates(image);

        // the name line used as the "distance" line: not a distance
        var pair = candidates.Pairs[0];
        var swapped = detector.Evaluate(image, candidates.Boxes[pair.Bottom], candidates.Boxes[pair.Top]);
        Assert.False(swapped.IsNameplate);

        // a box with no text
        var empty = detector.Evaluate(image, new Rect(5, 5, 60, 12), new Rect(5, 30, 60, 12));
        Assert.False(empty.IsNameplate);
    }

    /// <summary>
    /// One detector handles frame after frame of different sizes with the same buffers: the result for a frame does not depend on
    /// what came before it.
    /// </summary>
    [OpenCvFact]
    public void ReusingTheDetector_GivesTheSameResults()
    {
        var reader = new NameplateReader(Model);
        var frames = DetectorFixtures.Load();
        var images = frames.Select(f => Cv2.ImRead(f.ImagePath, ImreadModes.Color)).ToList();
        try
        {
            using var detector = new NameplateDetector(reader);
            var first = images.Select(i => detector.Detect(i)).ToList();
            for (var round = 0; round < 3; round++)
            {
                for (var i = images.Count - 1; i >= 0; i--) // another order than the first time
                {
                    var again = detector.Detect(images[i]);
                    Assert.Equal(first[i].Select(d => (d.Name, d.NameBox, d.DistanceBox, d.DistanceText)),
                        again.Select(d => (d.Name, d.NameBox, d.DistanceBox, d.DistanceText)));
                }
            }
        }
        finally
        {
            foreach (var image in images)
                image.Dispose();
        }
    }

    [OpenCvFact]
    public void FlatAndTinyFrames_GiveNothing()
    {
        var reader = new NameplateReader(Model);
        using var detector = new NameplateDetector(reader);
        using var flat = new Mat(1440, 2560, MatType.CV_8UC3, new Scalar(30, 30, 30));
        using var tiny = new Mat(8, 8, MatType.CV_8UC3, new Scalar(255, 255, 255));

        Assert.Empty(detector.Detect(flat));
        Assert.Empty(detector.Detect(tiny));
    }

    [OpenCvFact]
    public void WrongImageType_IsRejected()
    {
        var reader = new NameplateReader(Model);
        using var detector = new NameplateDetector(reader);
        using var gray = new Mat(100, 100, MatType.CV_8UC1, new Scalar(0));

        using var empty = new Mat();

        Assert.Throws<ArgumentException>(() => detector.Detect(gray));
        Assert.Throws<ArgumentException>(() => detector.Detect(empty));
    }

    [OpenCvFact]
    public void DisposedDetector_Throws()
    {
        var reader = new NameplateReader(Model);
        var detector = new NameplateDetector(reader);
        detector.Dispose();
        detector.Dispose(); // twice is fine
        using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(0, 0, 0));

        Assert.Throws<ObjectDisposedException>(() => detector.Detect(image));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static IEnumerable<(string Label, NameplateDetectorSettings Settings, ExpectedScan Expected)> Scans(DetectorFixtureFrame frame)
    {
        yield return ("default region", NameplateDetectorSettings.Default, frame.Default);
        yield return ("whole frame", WholeFrame, frame.WholeFrame);
    }

    private static bool Near(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= BoxTolerance && Math.Abs(a.Y - b.Y) <= BoxTolerance
        && Math.Abs(a.Width - b.Width) <= BoxTolerance && Math.Abs(a.Height - b.Height) <= BoxTolerance;

    private static int[] OrderOf(IReadOnlyList<Rect> boxes) =>
        Enumerable.Range(0, boxes.Count)
            .OrderBy(i => boxes[i].Y).ThenBy(i => boxes[i].X).ThenBy(i => boxes[i].Width).ThenBy(i => boxes[i].Height)
            .ToArray();

    private static void CompareCandidates(string where, ExpectedScan expected, NameplateCandidates actual, List<string> problems)
    {
        if (expected.Boxes.Length != actual.Boxes.Count)
        {
            problems.Add($"{where}: {actual.Boxes.Count} text lines, Python {expected.Boxes.Length}");
            return;
        }

        // compare in reading order, so that the pairs can be compared by the sorted position of their boxes
        var e = OrderOf(expected.Boxes);
        var a = OrderOf(actual.Boxes);
        var expectedRank = new int[e.Length];
        var actualRank = new int[a.Length];
        for (var k = 0; k < e.Length; k++)
        {
            expectedRank[e[k]] = k;
            actualRank[a[k]] = k;
            if (!Near(expected.Boxes[e[k]], actual.Boxes[a[k]]))
                problems.Add($"{where}: line {k} is {actual.Boxes[a[k]]}, Python {expected.Boxes[e[k]]}");
        }

        var expectedPairs = expected.Pairs.Select(p => (expectedRank[p.Top], expectedRank[p.Bottom])).OrderBy(p => p).ToArray();
        var actualPairs = actual.Pairs.Select(p => (actualRank[p.Top], actualRank[p.Bottom])).OrderBy(p => p).ToArray();
        if (!expectedPairs.SequenceEqual(actualPairs))
            problems.Add($"{where}: pairs {string.Join(",", actualPairs)}, Python {string.Join(",", expectedPairs)}");
        var expectedCrowd = expected.CrowdPairs.Select(p => (expectedRank[p.Top], expectedRank[p.Bottom], string.Join("+", p.Neighbours.Select(n => expectedRank[n]).OrderBy(n => n)))).OrderBy(p => p).ToArray();
        var actualCrowd = actual.CrowdPairs.Select(p => (actualRank[p.Top], actualRank[p.Bottom], string.Join("+", p.Neighbours.Select(n => actualRank[n]).OrderBy(n => n)))).OrderBy(p => p).ToArray();
        if (!expectedCrowd.SequenceEqual(actualCrowd))
            problems.Add($"{where}: crowd pairs {string.Join(",", actualCrowd)}, Python {string.Join(",", expectedCrowd)}");
        var expectedLoose = expected.LoosePairs.Select(p => (expectedRank[p.Top], expectedRank[p.Bottom])).OrderBy(p => p).ToArray();
        var actualLoose = actual.LoosePairs.Select(p => (actualRank[p.Top], actualRank[p.Bottom])).OrderBy(p => p).ToArray();
        if (!expectedLoose.SequenceEqual(actualLoose))
            problems.Add($"{where}: loose pairs {string.Join(",", actualLoose)}, Python {string.Join(",", expectedLoose)}");
    }

    private static void CompareDetections(string where, ExpectedDetection[] expected, IReadOnlyList<NameplateDetection> actual, List<string> problems)
    {
        var e = expected.OrderBy(d => d.NameBox.Y).ThenBy(d => d.NameBox.X).ToArray();
        var a = actual.OrderBy(d => d.NameBox.Y).ThenBy(d => d.NameBox.X).ToArray();
        if (e.Length != a.Length)
        {
            problems.Add($"{where}: {a.Length} nameplates ({string.Join(", ", a.Select(d => d.Name))}), Python {e.Length} ({string.Join(", ", e.Select(d => d.Name))})");
            return;
        }

        for (var i = 0; i < e.Length; i++)
        {
            if (e[i].Name != a[i].Name)
                problems.Add($"{where}: '{a[i].Name}', Python '{e[i].Name}'");
            else if (e[i].DistanceText != a[i].DistanceText)
                problems.Add($"{where}: {a[i].Name} distance read '{a[i].DistanceText}', Python '{e[i].DistanceText}'");
            else if (e[i].SpeedGroup != a[i].HasSpeedGroup)
                problems.Add($"{where}: {a[i].Name} speed group {a[i].HasSpeedGroup}, Python {e[i].SpeedGroup}");
            else if (!Near(e[i].NameBox, a[i].NameBox) || !Near(e[i].DistanceBox, a[i].DistanceBox))
                problems.Add($"{where}: {a[i].Name} boxes {a[i].NameBox} / {a[i].DistanceBox}, Python {e[i].NameBox} / {e[i].DistanceBox}");
            else if (Math.Abs(e[i].Score - a[i].Score) > ScoreTolerance)
                problems.Add($"{where}: {a[i].Name} score {a[i].Score}, Python {e[i].Score}");
        }
    }
}
