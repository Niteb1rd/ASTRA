using Iris.Core.Detection;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The tracker is pure logic (no images): these tests drive it with candidate boxes and a fake evaluation.</summary>
public class NameplateTrackerTests
{
    private const double Step = 100.0; // ms per frame (10 fps)

    private static Rect Top(int x = 1000, int y = 500, int w = 80, int h = 12) => new(x, y, w, h);

    private static TrackCandidate Pair(Rect top) => new(top, new Rect(top.X + 10, top.Y + 24, top.Width - 20, top.Height));

    /// <summary>Two identical reads, no time span: the old defaults, which keep the arithmetic of these tests simple.</summary>
    private static readonly NameplateTrackerSettings Fast = NameplateTrackerSettings.Default with { ConfirmReads = 2, ConfirmSpanMs = 0 };

    private sealed class Driver
    {
        public readonly NameplateTracker Tracker;
        public long Frame;
        public int Evaluations;
        public int FrameHeight; // 0: the reference height of the settings
        public Func<Rect, Rect, TrackEvaluation> Evaluate = (_, _) => TrackEvaluation.No;

        public Driver(NameplateTrackerSettings? settings = null) => Tracker = new NameplateTracker(settings ?? Fast);

        public TrackerUpdate Next(params TrackCandidate[] candidates)
        {
            var update = Tracker.Update(Frame, Frame * Step, candidates, (t, b) =>
            {
                Evaluations++;
                return Evaluate(t, b);
            }, FrameHeight);
            Frame++;
            return update;
        }
    }

    [Fact]
    public void NewCandidate_IsEvaluatedAtOnce_AndNotAgainWhileNotANameplate()
    {
        var d = new Driver();
        var plate = Pair(Top());

        for (var i = 0; i < 20; i++)
            d.Next(plate);

        Assert.Equal(1, d.Evaluations); // 2 seconds of frames: one evaluation, no re-read every frame
        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Equal(TrackVerdict.NotNameplate, track.Verdict);
        Assert.False(track.EverNameplate);
    }

    [Fact]
    public void NotNameplate_IsCheckedAgainAfterTheRecheckTime()
    {
        var d = new Driver();
        var plate = Pair(Top());

        for (var i = 0; i < 31; i++) // frames 0..30: 0 ms to 3000 ms
            d.Next(plate);

        Assert.Equal(2, d.Evaluations); // at 0 ms and at 3000 ms
    }

    [Fact]
    public void NotNameplate_IsCheckedAgainWhenTheBoxSizeChangesNotably()
    {
        var d = new Driver();
        d.Next(Pair(Top(w: 80)));
        d.Next(Pair(Top(w: 82)));  // 2 px wider: the same thing
        Assert.Equal(1, d.Evaluations);

        d.Next(Pair(Top(w: 96)));  // 16 px wider (more than 15 % of 80 plus 1 px): not the same thing
        Assert.Equal(2, d.Evaluations);

        d.Next(Pair(Top(w: 96, h: 15))); // taller by 3 px (of a 12 px line) is notable too
        Assert.Equal(3, d.Evaluations);
        Assert.Single(d.Tracker.Tracks); // still one track
    }

    [Fact]
    public void ANameIsConfirmedOnlyAfterItWasReadIdenticallyTwice()
    {
        var d = new Driver();
        d.Evaluate = (_, _) => new TrackEvaluation(true, "SYNTH_ALPHA");
        var plate = Pair(Top());

        var first = d.Next(plate);
        Assert.Empty(first.Confirmed);
        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Null(track.ConfirmedName);
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict);

        var second = d.Next(plate);
        Assert.Same(track, Assert.Single(second.Confirmed));
        Assert.Equal("SYNTH_ALPHA", track.ConfirmedName);
        Assert.Equal(0, track.FirstFrame);
        Assert.Equal(0, track.FirstReadFrame["SYNTH_ALPHA"]);
        Assert.Equal(1, track.ConfirmedFrame);
        Assert.Equal(Step, track.ConfirmedMs);
    }

    [Fact]
    public void ASingleMisread_NeverBecomesAName()
    {
        var d = new Driver();
        var reads = new Queue<string>(new[] { "SYNTH_ALPHA", "SYNTH_ALPNA", "SYNTH_ALPHA" });
        d.Evaluate = (_, _) => new TrackEvaluation(true, reads.Dequeue());
        var plate = Pair(Top());

        d.Next(plate);
        d.Next(plate);
        var track = d.Tracker.Tracks[0];
        Assert.Null(track.ConfirmedName); // A:1 B:1, nothing confirmed

        var third = d.Next(plate);
        Assert.Single(third.Confirmed);
        Assert.Equal("SYNTH_ALPHA", track.ConfirmedName); // A:2 beats B:1
        Assert.Equal(2, track.ConfirmedFrame);
        Assert.Equal(3, track.ReadCount);
    }

    [Fact]
    public void ARivalThatOnlyCatchesUp_DoesNotChangeTheConfirmedName()
    {
        var d = new Driver(Fast with { ConfirmedRereadMs = 0 });
        var reads = new Queue<string>(new[] { "AAAA", "AAAA", "BBBB", "BBBB" });
        d.Evaluate = (_, _) => new TrackEvaluation(true, reads.Dequeue());
        var plate = Pair(Top());

        d.Next(plate);
        d.Next(plate);
        var track = d.Tracker.Tracks[0];
        Assert.Equal("AAAA", track.ConfirmedName);

        d.Next(plate);
        d.Next(plate);
        Assert.Equal("AAAA", track.ConfirmedName); // A:2 B:2, a tie: the confirmed name stays (it is corrected, never withdrawn)
        Assert.Empty(track.Corrections);
    }

    [Fact]
    public void AName_ThatIsReadMoreOftenOverTheRecentWindow_CorrectsTheConfirmedOne()
    {
        var d = new Driver(Fast);
        var reads = new Queue<string>(new[] { "AAAA", "AAAA", "BBBB", "BBBB", "BBBB" });
        d.Evaluate = (_, _) => new TrackEvaluation(true, reads.Dequeue());
        var plate = Pair(Top());

        d.Next(plate);
        var confirming = d.Next(plate); // frame 1: A is confirmed
        var track = d.Tracker.Tracks[0];
        Assert.Same(track, Assert.Single(confirming.Confirmed));
        Assert.Empty(confirming.Corrected);
        Assert.Equal("AAAA", track.ConfirmedName);

        for (var i = 0; i < 9; i++) // frames 2..10: a confirmed track is read once a second
            d.Next(plate);
        Assert.Equal(2, d.Evaluations);

        d.Next(plate); // frame 11 (1100 ms): the first B
        Assert.Equal("AAAA", track.ConfirmedName);
        Assert.True(track.IsDisputed);   // its latest read disagrees: it is read at every frame now
        d.Next(plate); // frame 12: B twice, A twice: a tie
        Assert.Equal("AAAA", track.ConfirmedName);
        Assert.Empty(track.Corrections);

        var corrected = d.Next(plate); // frame 13: B three times against two
        Assert.Equal(5, d.Evaluations);  // 2 + frames 11, 12 and 13
        Assert.Same(track, Assert.Single(corrected.Confirmed));
        Assert.Same(track, Assert.Single(corrected.Corrected));
        Assert.Equal("BBBB", track.ConfirmedName);
        Assert.Equal(13, track.ConfirmedFrame);
        Assert.Equal(1300.0, track.ConfirmedMs);
        var correction = Assert.Single(track.Corrections);
        Assert.Equal(new NameCorrection("AAAA", "BBBB", 13, 1300.0), correction);
        Assert.Equal(new[] { "AAAA", "BBBB" }, track.Confirmations.Select(c => c.Name));
        Assert.False(track.IsDisputed);
    }

    [Fact]
    public void VotesOlderThanTheWindow_DoNotCountAgainstACorrection()
    {
        // A is confirmed at frame 1 by two votes (at 0 and 100 ms; a confirmed track is read only once a second, so frames 2 to 4 add none), then no
        // candidate until 2000 ms. B needs two votes, and A's two are outside the window of 1500 ms (with the default window of 4 seconds they would
        // tie B's two, and a tie corrects nothing)
        var d = new Driver(Fast with { CorrectionWindowMs = 1500, ExpireMs = 100000 });
        var name = "AAAA";
        d.Evaluate = (_, _) => new TrackEvaluation(true, name);
        var plate = Pair(Top());
        for (var i = 0; i < 5; i++)
            d.Next(plate);
        var track = d.Tracker.Tracks[0];
        Assert.Equal("AAAA", track.ConfirmedName);
        for (var i = 5; i < 20; i++)
            d.Next(); // no candidates: frames 5..19

        name = "BBBB";
        d.Next(plate); // frame 20 (2000 ms), the first B: A's votes (the last at 100 ms) are older than 2000 - 1500 ms and are dropped now
        Assert.Equal("AAAA", track.ConfirmedName);
        var update = d.Next(plate); // frame 21: two B against no A vote inside the window
        Assert.Same(track, Assert.Single(update.Corrected));
        Assert.Equal("BBBB", track.ConfirmedName);
    }

    [Fact]
    public void WithoutAWindow_AllVotesCountBeforeTheFirstConfirmation()
    {
        var d = new Driver(NameplateTrackerSettings.Default with { ConfirmReads = 3, ConfirmSpanMs = 0, CorrectionWindowMs = 0 });
        var reads = new Queue<string>(new[] { "AAAA", "BBBB", "AAAA", "BBBB", "AAAA" });
        d.Evaluate = (_, _) => new TrackEvaluation(true, reads.Dequeue());
        var plate = Pair(Top());
        for (var i = 0; i < 4; i++)
            d.Next(plate);
        Assert.Null(d.Tracker.Tracks[0].ConfirmedName); // A:2 B:2
        d.Next(plate);
        Assert.Equal("AAAA", d.Tracker.Tracks[0].ConfirmedName); // A:3 B:2
    }

    [Fact]
    public void VotesOlderThanFourConfirmSpans_AreDroppedBeforeTheFirstConfirmation()
    {
        // default settings: 3 reads over 300 ms; before the first confirmation only the votes of the last 4 x 300 = 1200 ms count
        var d = new Driver(NameplateTrackerSettings.Default with { ExpireMs = 100000 });
        var name = "BBBB";
        d.Evaluate = (_, _) => new TrackEvaluation(true, name);

        // five reads of B at five different widths (more than 3 px apart): no stable group, so B is never confirmed, but it is a rival of 5 votes
        foreach (var width in new[] { 100, 92, 84, 76, 68 })
            d.Next(Pair(Top(w: width))); // frames 0..4
        var track = d.Tracker.Tracks[0];
        Assert.Null(track.ConfirmedName);
        Assert.Equal(5, track.VoteWindowCount);

        for (var i = 5; i < 20; i++)
            d.Next(); // no candidates: frames 5..19

        name = "AAAA";
        var plate = Pair(Top(w: 80));
        d.Next(plate); // frame 20 (2000 ms): B's votes (at most 400 ms) are older than 2000 - 1200 ms and are dropped
        Assert.Equal(1, track.VoteWindowCount);
        d.Next(plate); // frame 21
        d.Next(plate); // frame 22: three reads of A, but over 200 ms only
        Assert.Null(track.ConfirmedName);

        var update = d.Next(plate); // frame 23 (2300 ms): four reads of A over 300 ms; without the cap B's five old votes would still outnumber them
        Assert.Same(track, Assert.Single(update.Confirmed));
        Assert.Equal("AAAA", track.ConfirmedName);
        Assert.Equal(23, track.ConfirmedFrame);
        Assert.Equal(9, track.ReadCount); // the count of reads is not cut, only the votes that still count
        Assert.Single(d.Tracker.Tracks);
    }

    [Fact]
    public void ALongTrackThatNeverConfirms_KeepsOnlyTheVotesOfTheLastFourConfirmSpans()
    {
        // two names alternate for a minute at changing widths (a triangle wave in steps of 10 px: no three reads of one name share a width within
        // 3 px), so nothing is ever confirmed; the votes held must stay bounded
        var d = new Driver(NameplateTrackerSettings.Default);
        var frame = 0;
        d.Evaluate = (_, _) => new TrackEvaluation(true, frame % 2 == 0 ? "AAAA" : "BBBB");

        var most = 0;
        for (frame = 0; frame < 600; frame++)
        {
            var phase = frame % 24;
            var level = phase <= 12 ? phase : 24 - phase;
            d.Next(Pair(Top(w: 60 + 10 * level)));
            most = Math.Max(most, d.Tracker.Tracks[0].VoteWindowCount);
        }

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Null(track.ConfirmedName);
        Assert.Equal(600, track.ReadCount);
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict);
        Assert.Equal(13, most); // the votes at 1200, 1100 ... 0 ms before the latest one (the limit is inclusive)
    }

    [Fact]
    public void WithoutAConfirmSpan_NoVoteIsDroppedBeforeTheFirstConfirmation()
    {
        Assert.Equal(1200.0, NameplateTrackerSettings.Default.PreConfirmationWindowMs);
        Assert.Equal(double.PositiveInfinity, (NameplateTrackerSettings.Default with { ConfirmSpanMs = 0 }).PreConfirmationWindowMs);
    }

    [Fact]
    public void ANameplateWithoutAVote_ClearsTheDispute()
    {
        var d = new Driver(Fast with { ConfirmedRereadMs = 0 }); // read at every frame
        var results = new Queue<TrackEvaluation>(new[]
        {
            new TrackEvaluation(true, "AAAA"),
            new TrackEvaluation(true, "AAAA"),
            new TrackEvaluation(true, "BBBB"),
            new TrackEvaluation(true, null),     // still a nameplate, but no vote (too unsure)
            new TrackEvaluation(true, "BBBB"),
            new TrackEvaluation(true, "-BBBB"),  // an edge hyphen: no vote either
        });
        d.Evaluate = (_, _) => results.Dequeue();
        var plate = Pair(Top());

        d.Next(plate);
        d.Next(plate);
        var track = d.Tracker.Tracks[0];
        Assert.Equal("AAAA", track.ConfirmedName);
        Assert.False(track.IsDisputed);

        d.Next(plate); // B against A: 1 to 2
        Assert.True(track.IsDisputed);

        d.Next(plate); // nameplate without a vote
        Assert.False(track.IsDisputed);
        Assert.Equal("AAAA", track.ConfirmedName);

        d.Next(plate); // B again: B has 2 votes, A has 2, a tie
        Assert.True(track.IsDisputed);

        d.Next(plate); // "-BBBB": no vote
        Assert.False(track.IsDisputed);
        Assert.Equal("AAAA", track.ConfirmedName);
    }

    [Fact]
    public void IdenticalReads_MustHaveTheSameBoxWidth_ToConfirm()
    {
        // the plate types its name out: the box grows while the text reads the same
        var d = new Driver(Fast with { ConfirmReads = 3, ConfirmSpanMs = 0 });
        d.Evaluate = (_, _) => new TrackEvaluation(true, "SYNTH_AL");

        d.Next(Pair(Top(w: 80)));
        d.Next(Pair(Top(w: 90)));
        d.Next(Pair(Top(w: 100)));
        var track = d.Tracker.Tracks[0];
        Assert.Equal(3, track.ReadCount);
        Assert.Null(track.ConfirmedName); // three identical reads, three widths

        d.Next(Pair(Top(w: 100)));
        Assert.Null(track.ConfirmedName); // two at 100
        d.Next(Pair(Top(w: 103))); // within 3 px of 100: the same width
        Assert.Equal("SYNTH_AL", track.ConfirmedName);
    }

    [Fact]
    public void TheWidthTolerance_IsScaledToTheFrameHeight()
    {
        TrackerUpdate Run(int frameHeight)
        {
            var d = new Driver(Fast with { ConfirmReads = 3, ConfirmSpanMs = 0 }) { FrameHeight = frameHeight };
            d.Evaluate = (_, _) => new TrackEvaluation(true, "SYNTH_AL");
            d.Next(Pair(Top(w: 80)));
            d.Next(Pair(Top(w: 84)));
            return d.Next(Pair(Top(w: 88)));  // 80, 84 and 88: each 4 px from the middle one
        }

        Assert.Empty(Run(0).Confirmed);         // 1440 high: 3 px
        Assert.Empty(Run(1440).Confirmed);
        Assert.Single(Run(2880).Confirmed);     // twice as high: 6 px
    }

    [Fact]
    public void StableGroup_IsTheLargestSetWithinTheToleranceOfOneRead()
    {
        var samples = new List<(double Ms, int Width)> { (0, 80), (100, 90), (200, 91), (300, 100), (400, 90) };

        Assert.Equal((3, 100.0, 400.0), NameplateTrack.StableGroup(samples, 2.0));   // 90, 91 and 90
        Assert.Equal((1, 0.0, 0.0), NameplateTrack.StableGroup(new List<(double, int)> { (0, 80) }, 2.0));
        Assert.Equal((0, 0.0, 0.0), NameplateTrack.StableGroup(new List<(double, int)>(), 2.0));
        // a tie goes to the later group
        Assert.Equal((2, 300.0, 400.0), NameplateTrack.StableGroup(new List<(double, int)> { (0, 80), (100, 80), (300, 120), (400, 120) }, 2.0));
    }

    [Theory]
    [InlineData("-PIL0TFISH", false)]
    [InlineData("ABCDEF-", false)]
    [InlineData("UNKNOWK_", true)]      // an underscore may start and end a name
    [InlineData("_ABCDEF", true)]
    [InlineData("CINDER-404", true)]
    [InlineData("T4PIR_220", true)]
    public void ANameThatStartsOrEndsWithAHyphen_IsNoVote(string name, bool confirmed)
    {
        var d = new Driver(Fast);
        d.Evaluate = (_, _) => new TrackEvaluation(true, name);
        var plate = Pair(Top());

        for (var i = 0; i < 5; i++)
            d.Next(plate);

        var track = d.Tracker.Tracks[0];
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict); // the plate is still a nameplate
        Assert.True(track.EverNameplate);
        Assert.Equal(confirmed ? name : null, track.ConfirmedName);
        Assert.Equal(confirmed ? 2 : 0, track.ReadCount); // a confirmed track is read once a second: two reads in these five frames
        if (!confirmed)
            Assert.Null(track.BestName);
    }

    [Theory]
    [InlineData("ABCD", true)]
    [InlineData("A-B_C", true)]
    [InlineData("-ABCD", false)]
    [InlineData("_ABCD", true)]
    [InlineData("ABCD-", false)]
    [InlineData("ABCD_", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NameShape_HasValidEdges(string? name, bool expected) => Assert.Equal(expected, NameShape.HasValidEdges(name));

    [Fact]
    public void AnEvaluationWithoutAName_KeepsTheVerdictButAddsNoVote()
    {
        var d = new Driver();
        d.Evaluate = (_, _) => new TrackEvaluation(true, null);
        var plate = Pair(Top());

        for (var i = 0; i < 5; i++)
            d.Next(plate);

        var track = d.Tracker.Tracks[0];
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict);
        Assert.True(track.EverNameplate);
        Assert.Equal(0, track.ReadCount);
        Assert.Null(track.BestName);
        Assert.Equal(5, d.Evaluations); // an unconfirmed nameplate is read every frame
    }

    [Fact]
    public void AConfirmedTrack_IsReadAgainOnlyEveryOnceInAWhile()
    {
        var d = new Driver();
        d.Evaluate = (_, _) => new TrackEvaluation(true, "SYNTH_ALPHA");
        var plate = Pair(Top());

        for (var i = 0; i < 25; i++) // 0 ms .. 2400 ms
            d.Next(plate);

        // confirmed at frame 1 (100 ms); next reads at 1100 ms and 2100 ms: 2 + 2 evaluations
        Assert.Equal(4, d.Evaluations);
        Assert.Equal("SYNTH_ALPHA", d.Tracker.Tracks[0].ConfirmedName);
        Assert.Equal(4, d.Tracker.Tracks[0].ReadCount);
    }

    [Fact]
    public void SeveralFailuresInARow_TurnANameplateIntoNotNameplate()
    {
        var d = new Driver();
        var ok = true;
        d.Evaluate = (_, _) => ok ? new TrackEvaluation(true, "SYNTH_ALPHA") : TrackEvaluation.No;
        var plate = Pair(Top());
        d.Next(plate);
        var track = d.Tracker.Tracks[0];
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict);

        ok = false;
        d.Next(plate);
        d.Next(plate);
        Assert.Equal(TrackVerdict.Nameplate, track.Verdict); // 2 failures
        d.Next(plate);
        Assert.Equal(TrackVerdict.NotNameplate, track.Verdict); // the third
        Assert.True(track.EverNameplate);
    }

    [Fact]
    public void AGoodEvaluation_ResetsTheFailureCount()
    {
        var d = new Driver();
        var script = new Queue<bool>(new[] { true, false, false, true, false, false });
        d.Evaluate = (_, _) => script.Dequeue() ? new TrackEvaluation(true, null) : TrackEvaluation.No;
        var plate = Pair(Top());
        for (var i = 0; i < 6; i++)
            d.Next(plate);

        Assert.Equal(TrackVerdict.Nameplate, d.Tracker.Tracks[0].Verdict);
    }

    [Fact]
    public void ATrackExpiresWhenNotSeen()
    {
        var d = new Driver();
        var plate = Pair(Top());
        d.Next(plate); // frame 0, 0 ms
        var track = d.Tracker.Tracks[0];

        for (var i = 1; i <= 10; i++) // up to 1000 ms: not yet (the limit is "more than 1000 ms")
        {
            var update = d.Next();
            Assert.Empty(update.Closed);
        }

        var closed = d.Next(); // 1100 ms
        Assert.Same(track, Assert.Single(closed.Closed));
        Assert.Empty(d.Tracker.Tracks);
    }

    [Fact]
    public void ACandidateThatMoves_KeepsItsTrack_ButAFarOneStartsANewTrack()
    {
        var d = new Driver();
        d.Next(Pair(Top(x: 1000)));
        var id = d.Tracker.Tracks[0].Id;

        d.Next(Pair(Top(x: 1020, y: 505))); // 3 line heights are 36 px: close enough
        Assert.Equal(id, Assert.Single(d.Tracker.Tracks).Id);

        d.Next(Pair(Top(x: 1020 + 200)));    // far away: a new object; the old track waits to expire
        Assert.Equal(2, d.Tracker.Tracks.Count);
        Assert.Equal(id, d.Tracker.Tracks[0].Id);
    }

    [Fact]
    public void TwoCandidates_AreMatchedToTheirOwnTracks()
    {
        var d = new Driver();
        d.Evaluate = (top, _) => new TrackEvaluation(true, top.Y < 510 ? "UPPER_ONE" : "LOWER_ONE");
        var a = Pair(Top(y: 500));
        var b = Pair(Top(y: 520)); // 20 px below: 1.7 line heights, closer than the move limit to each other's old place
        d.Next(a, b);
        d.Next(b, a); // the candidates arrive in the other order: each still finds its own track
        d.Next(a, b);

        // each track read its own name twice (and was confirmed): a swapped match would have mixed the names and confirmed nothing
        Assert.Equal(2, d.Tracker.Tracks.Count);
        var upper = Assert.Single(d.Tracker.Tracks, t => t.Top.Y == 500);
        var lower = Assert.Single(d.Tracker.Tracks, t => t.Top.Y == 520);
        Assert.Equal("UPPER_ONE", upper.ConfirmedName);
        Assert.Equal("LOWER_ONE", lower.ConfirmedName);
        Assert.Equal(2, upper.ReadCount);
        Assert.Equal(2, lower.ReadCount);
    }

    [Fact]
    public void TheEvaluationBudget_LimitsReadsPerFrame()
    {
        var d = new Driver(Fast with { MaxEvaluationsPerFrame = 3 });
        var plates = Enumerable.Range(0, 8).Select(i => Pair(Top(x: 200 + i * 300))).ToArray();

        d.Next(plates);
        Assert.Equal(3, d.Evaluations);
        Assert.Equal(3, d.Tracker.Tracks.Count);

        d.Next(plates);
        Assert.Equal(6, d.Evaluations);   // 3 more new ones; the 3 known ones are NotNameplate and are not read again
        d.Next(plates);
        Assert.Equal(8, d.Evaluations);
        Assert.Equal(8, d.Tracker.Tracks.Count);
    }

    [Fact]
    public void TheTrackLimit_DropsTheLeastRecentlySeen()
    {
        var d = new Driver(Fast with { MaxTracks = 2, ExpireMs = 100000 });
        var a = Pair(Top(x: 100));
        var b = Pair(Top(x: 500));
        var c = Pair(Top(x: 900));
        d.Next(a);
        d.Next(b);
        var update = d.Next(b, c);

        Assert.Equal(2, d.Tracker.Tracks.Count);
        var gone = Assert.Single(update.Closed);
        Assert.Equal(100, gone.Top.X); // a was seen the longest ago
    }

    [Fact]
    public void Finish_ReturnsAndForgetsTheLiveTracks()
    {
        var d = new Driver();
        d.Next(Pair(Top()));

        var all = d.Tracker.Finish();

        Assert.Single(all);
        Assert.Empty(d.Tracker.Tracks);
    }

    // ------------------------------------------------------------------------------------------------ fast movers

    private static Func<Rect, Rect, TrackEvaluation> Reads(string name) => (_, _) => new TrackEvaluation(true, name);

    /// <summary>
    /// A plate that moves 45 px per frame (more than the 36 px resting gate of a 12 px line) is followed: the first jump is recognised by its
    /// identical read, the next ones by the position the track is expected at (constant velocity), and it is confirmed.
    /// </summary>
    [Fact]
    public void AFastMovingNameplate_KeepsItsTrackAndIsConfirmed()
    {
        var d = new Driver();
        d.Evaluate = Reads("SYNTH_ALPHA");

        for (var i = 0; i < 6; i++)
            d.Next(Pair(Top(x: 1000 + 45 * i)));

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Equal("SYNTH_ALPHA", track.ConfirmedName);
        Assert.Equal(1, track.ConfirmedFrame);
        Assert.Equal(1, track.Jumps);          // only the first step needed the identical read; the others were predicted
        Assert.Equal(2, track.Evaluations);    // confirmed after two reads, then read again only once a second
        Assert.Equal(1000 + 45 * 5, track.Top.X);
    }

    /// <summary>Without the prediction and the identical-read rule (the old tracker) the same plate makes a new track every frame.</summary>
    [Fact]
    public void WithoutPredictionAndIdenticalReads_AFastPlateMakesANewTrackEveryFrame()
    {
        var d = new Driver(Fast with { SpeedGain = 0, IdenticalMs = 0 });
        d.Evaluate = Reads("SYNTH_ALPHA");

        for (var i = 0; i < 6; i++)
            d.Next(Pair(Top(x: 1000 + 45 * i)));

        Assert.Equal(6, d.Tracker.Tracks.Count);
    }

    [Fact]
    public void AnIdenticalRead_ContinuesATrackOnlyWithinTheTime()
    {
        NameplateTracker Run(int framesBetween, out int tracks)
        {
            var d = new Driver();
            d.Evaluate = Reads("SYNTH_ALPHA");
            d.Next(Pair(Top(x: 1000)));
            for (var i = 1; i < framesBetween; i++)
                d.Next();
            d.Next(Pair(Top(x: 1400))); // 400 px away
            tracks = d.Tracker.Tracks.Count;
            return d.Tracker;
        }

        var soon = Run(4, out var soonTracks);   // 400 ms later: still the same plate
        Assert.Equal(1, soonTracks);
        Assert.Equal(1, soon.Tracks[0].Jumps);
        Assert.Equal(2, soon.Tracks[0].ReadCount);

        Run(7, out var lateTracks);              // 700 ms later (more than IdenticalMs): a new plate
        Assert.Equal(2, lateTracks);
    }

    [Fact]
    public void AnIdenticalRead_NeedsTheSameSizeAndTheSameName()
    {
        var sized = new Driver();
        sized.Evaluate = Reads("SYNTH_ALPHA");
        sized.Next(Pair(Top(x: 1000)));
        sized.Next(Pair(Top(x: 1400, w: 120))); // far away and a much wider line: another plate
        Assert.Equal(2, sized.Tracker.Tracks.Count);

        var named = new Driver();
        var names = new Queue<string>(new[] { "AAAA_ONE", "BBBB_TWO" });
        named.Evaluate = (_, _) => new TrackEvaluation(true, names.Dequeue());
        named.Next(Pair(Top(x: 1000)));
        named.Next(Pair(Top(x: 1400))); // far away, another name: another plate
        Assert.Equal(2, named.Tracker.Tracks.Count);
    }

    /// <summary>A track that is not a nameplate is never continued by an identical read (there is no read to compare with).</summary>
    [Fact]
    public void AJumpIsNotFollowed_WhenTheTrackIsNoNameplate()
    {
        var d = new Driver(); // evaluations say "no nameplate"
        d.Next(Pair(Top(x: 1000)));
        d.Next(Pair(Top(x: 1400)));

        Assert.Equal(2, d.Tracker.Tracks.Count);
        Assert.All(d.Tracker.Tracks, t => Assert.Equal(0, t.Jumps));
    }

    /// <summary>Two plates with the same read jump at once: each detection continues one track, the nearest first, and no track is used twice.</summary>
    [Fact]
    public void TwoJumpingPlates_WithTheSameRead_ClaimOneTrackEach()
    {
        var d = new Driver();
        d.Evaluate = Reads("SAME_NAME");
        d.Next(Pair(Top(x: 300)), Pair(Top(x: 800)));
        Assert.Equal(new[] { 1, 2 }, d.Tracker.Tracks.Select(t => t.Id));

        d.Next(Pair(Top(x: 520)), Pair(Top(x: 1060)));

        Assert.Equal(2, d.Tracker.Tracks.Count); // no new track
        var first = d.Tracker.Tracks.Single(t => t.Id == 1);
        var second = d.Tracker.Tracks.Single(t => t.Id == 2);
        Assert.Equal(520, first.Top.X);   // 220 px from track 1, 280 from track 2: nearest wins
        Assert.Equal(1060, second.Top.X); // track 1 is taken, so the other detection continues track 2
        Assert.Equal(1, first.Jumps);
        Assert.Equal(1, second.Jumps);
    }

    /// <summary>The gate around the expected position grows with the speed of the track (a step a little larger than predicted is still the same plate), up to MaxGateLines.</summary>
    [Fact]
    public void TheGate_GrowsWithTheSpeedOfTheTrack_ButIsBounded()
    {
        int TracksAfter(NameplateTrackerSettings settings)
        {
            var d = new Driver(settings);
            // 30 px per frame (inside the resting gate of 36 px), then a step of 90 px: 60 px from the expected place, gate 36 + 30
            foreach (var x in new[] { 1000, 1030, 1060, 1150 })
                d.Next(Pair(Top(x: x)));
            return d.Tracker.Tracks.Count;
        }

        Assert.Equal(1, TracksAfter(Fast));
        Assert.Equal(2, TracksAfter(Fast with { SpeedGain = 0 }));      // no growth: the old gate
        Assert.Equal(2, TracksAfter(Fast with { MaxGateLines = 4 }));   // bounded to 48 px
    }

    /// <summary>The expected position needs two associations: a track seen once has no velocity and gets only the resting gate.</summary>
    [Fact]
    public void ATrackSeenOnce_HasNoVelocity()
    {
        var d = new Driver();
        d.Next(Pair(Top(x: 1000)));
        d.Next(Pair(Top(x: 1045))); // 45 px: outside the resting gate, nothing to predict from

        Assert.Equal(2, d.Tracker.Tracks.Count);
    }

    [Fact]
    public void Defaults_AreThreeReadsOverThreeHundredMilliseconds()
    {
        Assert.Equal(3, NameplateTrackerSettings.Default.ConfirmReads);
        Assert.Equal(300.0, NameplateTrackerSettings.Default.ConfirmSpanMs);
        Assert.Equal(3.0, NameplateTrackerSettings.Default.StableWidthPx);
        Assert.Equal(1440, NameplateTrackerSettings.Default.ReferenceFrameHeight);
        Assert.Equal(4000.0, NameplateTrackerSettings.Default.CorrectionWindowMs);
    }

    [Fact]
    public void ConfirmSpan_KeepsAnAnimationStageFromBeingConfirmed()
    {
        // the game types a name out: a stage of it is read identically in a few frames in a row, then the next stage comes
        var d = new Driver(NameplateTrackerSettings.Default with { ConfirmReads = 2, ConfirmSpanMs = 300 });
        d.Evaluate = (_, _) => new TrackEvaluation(true, "SYNTH_AL");
        var plate = Pair(Top());
        d.Next(plate);
        d.Next(plate);
        d.Next(plate); // three reads, 200 ms: not enough
        Assert.Null(d.Tracker.Tracks[0].ConfirmedName);

        d.Next(plate); // 300 ms
        Assert.Equal("SYNTH_AL", d.Tracker.Tracks[0].ConfirmedName);
    }

    [Fact]
    public void Settings_RejectNonsense()
    {
        Assert.Null(NameplateTrackerSettings.Default.Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { ConfirmReads = 0 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { ExpireMs = -1 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { MaxTracks = 0 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { MoveLines = 0 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { SpeedGain = -1 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { MaxGateLines = 0 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { IdenticalMs = -1 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { MaxSpeedDtMs = -1 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { StableWidthPx = -1 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { ReferenceFrameHeight = 0 }).Validate());
        Assert.NotNull((NameplateTrackerSettings.Default with { CorrectionWindowMs = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => new NameplateTracker(NameplateTrackerSettings.Default with { ConfirmReads = 0 }));
    }
}
