using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// O/0 evidence across frames (<see cref="NameplateTrackerSettings.O0MultiFrame"/>, on by default, tracker_ref.o0_llr / o0_resolve); exclusion matching stays exact. Pure logic: sequences of P(O) values and fake evaluations, no images. The sequences are the twelve of the training kit's
/// <c>fixtures-o0mf/sequences.json</c> (reads and probabilities only), except that the read of "flipping_reads_one_vote", which in the kit is the test character's own handle, is replaced
/// here by a made-up name with its O/0 flips at the same positions (the totals and the decisions do not depend on the other characters).
/// </summary>
public class O0MultiFrameTests
{
    // ------------------------------------------------------------------------------------------------ the sequences of the reference

    private sealed record Frame(string Read, (int, double)[] P);

    private sealed record Case(string Name, Frame[] Frames, string[] Expected, (int, double)[] Totals);

    private static readonly Case[] Cases =
    {
        new Case("one_confident_frame_decides",
            new[] { new Frame("_OX7K_", new (int, double)[] { (1, 0.99) }), new Frame("_OX7K_", new (int, double)[] { (1, 0.5) }) },
            new[] { "_OX7K_", "_OX7K_" },
            new (int, double)[] { (1, 4.0) }),
        new Case("two_moderate_frames",
            new[] { new Frame("AO1", new (int, double)[] { (1, 0.9) }), new Frame("A01", new (int, double)[] { (1, 0.9) }), new Frame("AO1", new (int, double)[] { (1, 0.9) }) },
            new[] { "AO1", "AO1", "AO1" },
            new (int, double)[] { (1, 6.591673732) }),
        new Case("two_moderate_frames_zero",
            new[] { new Frame("XO9", new (int, double)[] { (1, 0.1) }), new Frame("XO9", new (int, double)[] { (1, 0.1) }) },
            new[] { "XO9", "X09" },
            new (int, double)[] { (1, -4.394449155) }),
        new Case("near_threshold_below",
            new[] { new Frame("N0N", new (int, double)[] { (1, 0.949) }) },
            new[] { "N0N" },
            new (int, double)[] { (1, 2.923583166) }),
        new Case("near_threshold_above",
            new[] { new Frame("N0N", new (int, double)[] { (1, 0.951) }) },
            new[] { "NON" },
            new (int, double)[] { (1, 2.965693764) }),
        new Case("flipping_reads_one_vote",
            new[]
            {
                new Frame("SOOTOX0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", new (int, double)[] { (1, 0.85), (2, 0.3), (4, 0.4) }),
                new Frame("S00T0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", new (int, double)[] { (1, 0.7), (2, 0.2), (4, 0.2) }),
                new Frame("S0OT0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", new (int, double)[] { (1, 0.9), (2, 0.3), (4, 0.1) }),
                new Frame("SO0TOX0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", new (int, double)[] { (1, 0.6), (2, 0.25), (4, 0.3) }),
                new Frame("SO0T0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", new (int, double)[] { (1, 0.8), (2, 0.1), (4, 0.2) }),
            },
            new[]
            {
                "SOOTOX0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", "S00T0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", "SO0T0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_",
                "SO0T0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_", "SO0T0X0_SYNTH_PLATE_ALPHA_BETA_GAMMA_",
            },
            new (int, double)[] { (1, 6.570882962), (2, -6.376726948), (4, -6.222576268) }),
        new Case("decided_then_contrary",
            new[]
            {
                new Frame("QO", new (int, double)[] { (1, 0.99) }), new Frame("QO", new (int, double)[] { (1, 0.05) }), new Frame("QO", new (int, double)[] { (1, 0.05) }),
                new Frame("QO", new (int, double)[] { (1, 0.05) }), new Frame("QO", new (int, double)[] { (1, 0.05) }),
            },
            new[] { "QO", "QO", "QO", "Q0", "Q0" },
            new (int, double)[] { (1, -7.777755917) }),
        new Case("exact_p_zero_one",
            new[] { new Frame("A0B0", new (int, double)[] { (1, 0.0), (3, 1.0) }), new Frame("A0B0", new (int, double)[] { (1, 0.0), (3, 1.0) }) },
            new[] { "A0BO", "A0BO" },
            new (int, double)[] { (1, -8.0), (3, 8.0) }),
        new Case("undecided_keeps_read_char",
            new[] { new Frame("ZO0", new (int, double)[] { (1, 0.8), (2, 0.3) }), new Frame("Z00", new (int, double)[] { (1, 0.3), (2, 0.3) }), new Frame("ZOO", new (int, double)[] { (1, 0.6), (2, 0.6) }) },
            new[] { "ZO0", "Z00", "ZOO" },
            new (int, double)[] { (1, 0.944461609), (2, -1.289130613) }),
        new Case("unmeasured_position_keeps_decision",
            new[] { new Frame("A0A", new (int, double)[] { (1, 0.001) }), new Frame("AOA", Array.Empty<(int, double)>()), new Frame("A0A", Array.Empty<(int, double)>()) },
            new[] { "A0A", "A0A", "A0A" },
            new (int, double)[] { (1, -4.0) }),
        new Case("separate_keys",
            new[] { new Frame("A07", new (int, double)[] { (1, 0.99) }), new Frame("K07", new (int, double)[] { (1, 0.6) }), new Frame("A07", new (int, double)[] { (1, 0.5) }) },
            new[] { "AO7", "K07", "AO7" },
            new (int, double)[] { (1, 4.0) }),
        new Case("p_for_non_o0_position_ignored",
            new[] { new Frame("ABO", new (int, double)[] { (0, 0.99), (1, 0.99), (2, 0.99) }) },
            new[] { "ABO" },
            new (int, double)[] { (2, 4.0) }),
    };

    public static IEnumerable<object[]> CaseNames => Cases.Select(c => new object[] { c.Name });

    private static List<O0Glyph> Glyphs(Frame frame) => frame.P.Select(p => new O0Glyph(p.Item1, p.Item2)).ToList();

    [Fact]
    public void TheSequences_AreAllThere() => Assert.Equal(12, Cases.Length);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TheVotedNameAfterEveryFrame_AndTheTotals_AreThoseOfThePythonReference(string caseName)
    {
        var c = Cases.Single(x => x.Name == caseName);
        var table = new O0EvidenceTable();

        for (var i = 0; i < c.Frames.Length; i++)
            Assert.Equal(c.Expected[i], table.Resolve(c.Frames[i].Read, Glyphs(c.Frames[i])));

        // the totals of the key of the last read: the listed positions agree with the reference, every other position has none
        var totals = table.TotalsOf(c.Frames[^1].Read);
        Assert.NotNull(totals);
        var expected = c.Totals.ToDictionary(t => t.Item1, t => t.Item2);
        for (var pos = 0; pos < totals!.Length; pos++)
            Assert.Equal(expected.GetValueOrDefault(pos), totals[pos], 6);
    }

    // ------------------------------------------------------------------------------------------------ the numbers and the helper

    [Fact]
    public void TheNumbers_AreTheOnesOfTheReference()
    {
        Assert.Equal(4.0, O0EvidenceTable.LlrClip);
        Assert.Equal(19.0, O0EvidenceTable.DecideOdds);
        Assert.Equal(16, O0EvidenceTable.MaxKeys);
        Assert.Equal(2.9444389791664403, O0EvidenceTable.DecideLlr);
        Assert.Equal(O0EvidenceTable.DecideLlr, Math.Log(O0EvidenceTable.DecideOdds), 14);
    }

    [Fact]
    public void Llr_IsTheClippedLogOdds_ExactZeroAndOneGiveTheClip()
    {
        Assert.Equal(0.0, O0EvidenceTable.Llr(0.5), 12);
        Assert.Equal(Math.Log(0.9 / 0.1), O0EvidenceTable.Llr(0.9), 12);
        Assert.Equal(-Math.Log(0.9 / 0.1), O0EvidenceTable.Llr(0.1), 12);
        Assert.Equal(4.0, O0EvidenceTable.Llr(0.99));     // 4.595 clipped
        Assert.Equal(-4.0, O0EvidenceTable.Llr(0.01));
        Assert.Equal(4.0, O0EvidenceTable.Llr(1.0));      // no log of zero
        Assert.Equal(-4.0, O0EvidenceTable.Llr(0.0));
        Assert.Equal(4.0, O0EvidenceTable.Llr(1.5));      // out of range stays at the clip
        Assert.Equal(-4.0, O0EvidenceTable.Llr(-0.5));
        Assert.Equal(0.0, O0EvidenceTable.Llr(double.NaN));
    }

    [Fact]
    public void Resolve_AGlyphOutsideTheNameOrOnAnotherCharacter_IsIgnored()
    {
        var table = new O0EvidenceTable();

        var name = table.Resolve("ABO", new[] { new O0Glyph(-1, 0.99), new O0Glyph(3, 0.99), new O0Glyph(7, 0.99), new O0Glyph(0, 0.99), new O0Glyph(2, 0.99) });

        Assert.Equal("ABO", name);
        Assert.Equal(new[] { 0.0, 0.0, 4.0 }, table.TotalsOf("ABO"));
    }

    [Fact]
    public void Resolve_WithoutGlyphs_TheEarlierTotalsStillDecide_AndNothingIsAdded()
    {
        var table = new O0EvidenceTable();
        table.Resolve("A0A", new[] { new O0Glyph(1, 0.001) });

        Assert.Equal("A0A", table.Resolve("AOA", null));
        Assert.Equal("A0A", table.Resolve("AOA", Array.Empty<O0Glyph>()));
        Assert.Equal(new[] { 0.0, -4.0, 0.0 }, table.TotalsOf("AOA"));
        Assert.Null(table.TotalsOf("ZZZ"));
    }

    [Fact]
    public void Resolve_KeepsAtMostSixteenKeys_TheOldestGoesFirst_AKnownKeyEvictsNothing()
    {
        var table = new O0EvidenceTable();
        for (var i = 0; i < 16; i++)
            table.Resolve($"K{i:00}O", new[] { new O0Glyph(3, 0.9) });
        Assert.Equal(16, table.KeyCount);

        table.Resolve("K00O", new[] { new O0Glyph(3, 0.9) });   // a known key: nothing is dropped
        Assert.Equal(16, table.KeyCount);
        Assert.Equal(2 * Math.Log(9.0), table.TotalsOf("K00O")![3], 9);

        table.Resolve("K99O", new[] { new O0Glyph(3, 0.9) });   // a new one: the oldest key (K00O) goes, the evidence of the others stays
        Assert.Equal(16, table.KeyCount);
        Assert.Null(table.TotalsOf("K00O"));
        Assert.NotNull(table.TotalsOf("K01O"));
        Assert.NotNull(table.TotalsOf("K99O"));
    }

    // ------------------------------------------------------------------------------------------------ the tracker

    private const double Step = 100.0;

    private static readonly NameplateTrackerSettings Fast = NameplateTrackerSettings.Default with { ConfirmReads = 2, ConfirmSpanMs = 0, ConfirmedRereadMs = 0 };

    private static TrackCandidate Pair() => new(new Rect(1000, 500, 80, 12), new Rect(1010, 524, 60, 12));

    private sealed class Driver
    {
        public readonly NameplateTracker Tracker;
        public readonly SessionExclusions Session = new();
        public long Frame;

        public Driver(NameplateTrackerSettings settings) => Tracker = new NameplateTracker(settings, Session);

        public TrackerUpdate Next(TrackEvaluation read)
        {
            var update = Tracker.Update(Frame, Frame * Step, new[] { Pair() }, (_, _) => read);
            Frame++;
            return update;
        }
    }

    /// <summary>One plate whose O/0 at index 8 flips from frame to frame: the model's read alternates, the measured P(O) says 0 (0.02) or is unsure (0.2).</summary>
    private static TrackEvaluation Alternating(int frame, double p) =>
        new(true, frame % 2 == 0 ? "SYNTH_PL0T" : "SYNTH_PLOT", null, null, new[] { new O0Glyph(8, p) });

    [Fact]
    public void O0MultiFrame_IsOnByDefault()
    {
        Assert.True(NameplateTrackerSettings.Default.O0MultiFrame);
    }

    [Fact]
    public void AlternatingReads_VoteForOneName_WhenO0MultiFrameIsOn()
    {
        var d = new Driver(Fast);

        for (var i = 0; i < 6; i++)
            d.Next(Alternating(i, 0.02)); // P(O) 0.02: log odds -3.9, sure it is a zero in one frame

        var track = Assert.Single(d.Tracker.Tracks);
        var read = Assert.Single(track.Reads);
        Assert.Equal("SYNTH_PL0T", read.Name);
        Assert.Equal(6, read.Count);
        Assert.Equal("SYNTH_PL0T", track.ConfirmedName);
        Assert.Equal(1, track.ConfirmedFrame); // the second vote confirms it
        Assert.Empty(track.Corrections);
    }

    [Fact]
    public void TheSameReads_SplitTheirVotes_WhenO0MultiFrameIsOff()
    {
        var d = new Driver(Fast with { O0MultiFrame = false });

        for (var i = 0; i < 6; i++)
            d.Next(Alternating(i, 0.02));

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Equal(new[] { "SYNTH_PL0T", "SYNTH_PLOT" }, track.Reads.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(track.Reads, r => Assert.Equal(3, r.Count));
        Assert.Equal(2, track.ConfirmedFrame); // only the third read breaks the tie
    }

    [Fact]
    public void UnsureFrames_KeepTheReadsCharacterUntilTheTotalsDecide()
    {
        var d = new Driver(Fast);

        for (var i = 0; i < 6; i++)
            d.Next(Alternating(i, 0.2)); // log odds -1.39 per frame: totals -1.39 (frame 0) and -2.77 (frame 1) are undecided, from frame 2 (-4.16) the position is a 0

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Equal(1, track.Reads.Single(r => r.Name == "SYNTH_PLOT").Count);  // frame 1: undecided, so its own character, the letter
        Assert.Equal(5, track.Reads.Single(r => r.Name == "SYNTH_PL0T").Count);  // frame 0 (its own character) and frames 2 to 5 (decided)
        Assert.Equal("SYNTH_PL0T", track.ConfirmedName);
        Assert.Equal(2, track.ConfirmedFrame);
    }

    [Fact]
    public void EvidenceIsKeptPerTrack_AnotherPlatesFramesDoNotDecideThisOne()
    {
        var tracker = new NameplateTracker(Fast);
        var sure = new Rect(1000, 500, 80, 12);
        var unsure = new Rect(1400, 500, 80, 12);
        TrackCandidate At(Rect top) => new(top, new Rect(top.X + 10, top.Y + 24, top.Width - 20, top.Height));

        tracker.Update(0, 0, new[] { At(sure), At(unsure) }, (top, _) => top.X == sure.X
            ? new TrackEvaluation(true, "SYNTH_PL0T", null, null, new[] { new O0Glyph(8, 0.02) })    // sure it is a zero
            : new TrackEvaluation(true, "SYNTH_PLOT", null, null, new[] { new O0Glyph(8, 0.5) }));   // no opinion: the read as it is

        Assert.Equal("SYNTH_PL0T", tracker.Tracks[0].Reads.Single().Name);
        Assert.Equal("SYNTH_PLOT", tracker.Tracks[1].Reads.Single().Name);
    }

    [Fact]
    public void TheResolvedName_IsUsedForTheSessionExclusionCheck_WithAnExactComparison()
    {
        var d = new Driver(Fast);
        d.Session.TryAdd("SYNTH_PL0T"); // excluded for the session, spelled with the zero

        for (var i = 0; i < 4; i++)
            d.Next(Alternating(i, 0.02)); // the frames that read the letter O are resolved to the zero first, so they are the excluded name too

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Empty(track.Reads);
        Assert.Null(track.ConfirmedName);
        Assert.True(track.DuplicateExcluded);
    }

    /// <summary>A plate that jumped (400 px, more than the resting gate) and is read with the other O/0 spelling is still the same track: it keeps its evidence. Only this continuity match folds O/0.</summary>
    private static NameplateTracker JumpWithOtherSpelling(NameplateTrackerSettings settings)
    {
        var tracker = new NameplateTracker(settings);
        TrackCandidate At(int x) => new(new Rect(x, 500, 80, 12), new Rect(x + 10, 524, 60, 12));
        var reads = new Queue<string>(new[] { "SYNTH_PL0T", "SYNTH_PLOT" });
        Func<Rect, Rect, TrackEvaluation> evaluate = (_, _) => new TrackEvaluation(true, reads.Dequeue(), null, null, new[] { new O0Glyph(8, 0.02) });
        tracker.Update(0, 0, new[] { At(1000) }, evaluate);
        tracker.Update(1, Step, new[] { At(1400) }, evaluate);
        return tracker;
    }

    [Fact]
    public void ALostTrack_IsFoundAgainByTheOtherO0Spelling_AndKeepsItsEvidence()
    {
        var tracker = JumpWithOtherSpelling(Fast);

        var track = Assert.Single(tracker.Tracks);
        Assert.Equal(1, track.Jumps);
        var read = Assert.Single(track.Reads);
        Assert.Equal("SYNTH_PL0T", read.Name); // the letter O of the second read was decided by the first frame's evidence plus its own
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public void ALostTrack_IsNotFoundByTheOtherSpelling_WhenO0MultiFrameIsOff()
    {
        var tracker = JumpWithOtherSpelling(Fast with { O0MultiFrame = false });

        Assert.Equal(2, tracker.Tracks.Count);
    }

    [Fact]
    public void ANameWithoutEvidenceOrWithoutO0_IsVotedAsRead()
    {
        var d = new Driver(Fast);

        d.Next(new TrackEvaluation(true, "SYNTH_ALPHA"));
        d.Next(new TrackEvaluation(true, "SYNTH_ALPHA", null, null, new[] { new O0Glyph(3, 0.001) })); // a glyph on a T: ignored

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Equal("SYNTH_ALPHA", Assert.Single(track.Reads).Name);
        Assert.Equal(2, track.Reads[0].Count);
    }

    [Fact]
    public void ANameThatMayNotStartOrEndWithAHyphen_AddsNoEvidence()
    {
        var d = new Driver(Fast);

        d.Next(new TrackEvaluation(true, "-SYNTH_PL0T", null, null, new[] { new O0Glyph(9, 0.001) })); // an edge hyphen: no vote, and the check comes before the evidence

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Empty(track.Reads);
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict);
    }

    // ------------------------------------------------------------------------------------------------ exclusions stay exact

    [Fact]
    public void SessionExclusions_MatchExactly_AMisreadOfAnExcludedNameIsNotExcluded()
    {
        var session = new SessionExclusions();
        Assert.True(session.TryAdd("UNKNOWN"));
        Assert.True(session.Contains(" unknown "));
        Assert.False(session.Contains("UNKN0WN")); // looked up like any name; the white duplicate rule still catches simultaneous misreads
        Assert.True(session.TryAdd("UNKN0WN"));
    }

    // ------------------------------------------------------------------------------------------------ the reader's evidence (with the native OpenCV library)

    private static readonly LetterModel Model = LetterModel.LoadEmbedded();

    [OpenCvFact]
    public void TheDetection_CarriesTheO0Evidence_IndexedInTheJoinedName()
    {
        var frame = PlateFixtures.Load().Single(f => f.File == "plates_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model));

        var found = detector.Detect(image).ToDictionary(d => d.Name);

        // the Python reference measured the O of SINGLEROWPLATE (index 7) and the O of SECOND in the second row of FIRSTLAYERSAMPLE + SECONDLAYERHANDLE: 16 letters in the first row, S E C, so index 19
        var single = found["SINGLEROWPLATE"];
        Assert.Contains(single.O0Evidence!, g => g.Index == 7 && g.ProbabilityO > 0.95);
        var two = found["FIRSTLAYERSAMPLESECONDLAYERHANDLE"];
        Assert.True(two.IsMultiRow);
        Assert.Contains(two.O0Evidence!, g => g.Index == 19 && g.ProbabilityO > 0.95);
        foreach (var detection in found.Values.Where(d => d.O0Evidence is not null))
            Assert.All(detection.O0Evidence!, g => Assert.True(detection.Name[g.Index] is 'O' or '0' && g.ProbabilityO is >= 0.0 and <= 1.0));
    }

    [OpenCvFact]
    public void WithoutTheSubPixelRule_ThereIsNoEvidence()
    {
        var frame = PlateFixtures.Load().Single(f => f.File == "plates_a.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(Model), NameplateDetectorSettings.Default with { SubPixelO0Rule = false });

        var found = detector.Detect(image);

        Assert.NotEmpty(found);
        Assert.All(found, d => Assert.Null(d.O0Evidence));
    }
}
