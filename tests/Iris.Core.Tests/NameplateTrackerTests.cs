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
        public Func<Rect, Rect, TrackEvaluation> Evaluate = (_, _) => TrackEvaluation.No;

        public Driver(NameplateTrackerSettings? settings = null) => Tracker = new NameplateTracker(settings ?? Fast);

        public TrackerUpdate Next(params TrackCandidate[] candidates)
        {
            var update = Tracker.Update(Frame, Frame * Step, candidates, (t, b) =>
            {
                Evaluations++;
                return Evaluate(t, b);
            });
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

        d.Next(Pair(Top(w: 92)));  // 15% wider: not the same thing
        Assert.Equal(2, d.Evaluations);

        d.Next(Pair(Top(w: 92, h: 15))); // taller by 3 px (of a 12 px line) is notable too
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
    public void ARivalThatCatchesUp_TakesTheConfirmationBack()
    {
        var d = new Driver(Fast with { ConfirmedRereadMs = 0 });
        var reads = new Queue<string>(new[] { "AAAA", "AAAA", "BBBB", "BBBB", "BBBB" });
        d.Evaluate = (_, _) => new TrackEvaluation(true, reads.Dequeue());
        var plate = Pair(Top());

        d.Next(plate);
        d.Next(plate);
        var track = d.Tracker.Tracks[0];
        Assert.Equal("AAAA", track.ConfirmedName);

        d.Next(plate);
        Assert.Equal("AAAA", track.ConfirmedName); // A:2 B:1
        d.Next(plate);
        Assert.Null(track.ConfirmedName);           // A:2 B:2, a tie: nobody is trusted
        d.Next(plate);
        Assert.Equal("BBBB", track.ConfirmedName);  // B:3
        Assert.Equal(new[] { "AAAA", "BBBB" }, track.Confirmations.Select(c => c.Name));
    }

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
        d.Evaluate = (top, _) => new TrackEvaluation(true, top.Y < 600 ? "UPPER_ONE" : "LOWER_ONE");
        var a = Pair(Top(y: 500));
        var b = Pair(Top(y: 520)); // 20 px below: 1.7 line heights, closer than the move limit to each other's old place
        d.Next(a, b);
        d.Next(b, a); // the candidates arrive in the other order: each still finds its own track
        d.Next(a, b);

        Assert.Equal(2, d.Tracker.Tracks.Count);
        Assert.All(d.Tracker.Tracks, t => Assert.Equal(3, t.Evaluations));
        Assert.Equal(new[] { "UPPER_ONE", "UPPER_ONE" }.Length, d.Tracker.Tracks.Count);
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

    [Fact]
    public void Defaults_AreThreeReadsOverThreeHundredMilliseconds()
    {
        Assert.Equal(3, NameplateTrackerSettings.Default.ConfirmReads);
        Assert.Equal(300.0, NameplateTrackerSettings.Default.ConfirmSpanMs);
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
        Assert.Throws<ArgumentException>(() => new NameplateTracker(NameplateTrackerSettings.Default with { ConfirmReads = 0 }));
    }
}
