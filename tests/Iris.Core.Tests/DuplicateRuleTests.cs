using Iris.Core.Detection;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The duplicate rule of the tracker (port of <c>_duplicate</c> of tracker_ref.py): one name read on 2 or more separate WHITE plates by both tracks in the same update, within 3
/// updates, is excluded for the session (an in-memory list, never written to the exclusion file); a name on one of the user's lists is never excluded; blue and green plates (party
/// members appear as both) are not counted. Pure logic: fake evaluations, no images.
/// </summary>
public class DuplicateRuleTests
{
    private const double Step = 100.0;

    private static readonly NameplateTrackerSettings Settings = NameplateTrackerSettings.Default with { ConfirmReads = 2, ConfirmSpanMs = 0, ConfirmedRereadMs = 0 };

    private static Rect Top(int x, int y = 500) => new(x, y, 80, 12);

    private static TrackCandidate Pair(Rect top) => new(top, new Rect(top.X + 10, top.Y + 24, top.Width - 20, top.Height));

    private sealed class Driver
    {
        public readonly NameplateTracker Tracker;
        public readonly SessionExclusions Session;
        public long Frame;
        public Func<string, bool>? IsListed;
        public Func<Rect, TrackEvaluation> Evaluate = _ => TrackEvaluation.No;

        public Driver(SessionExclusions? session = null, NameplateTrackerSettings? settings = null)
        {
            Session = session ?? new SessionExclusions();
            Tracker = new NameplateTracker(settings ?? Settings, Session);
        }

        public TrackerUpdate Next(params TrackCandidate[] candidates)
        {
            var update = Tracker.Update(Frame, Frame * Step, candidates, (t, _) => Evaluate(t), 0, IsListed);
            Frame++;
            return update;
        }
    }

    private static TrackEvaluation Read(string name, PlateColor? color = PlateColor.White) => new(true, name, null, color);

    [Fact]
    public void TheSameNameOnTwoWhitePlates_IsExcludedForTheSession()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("DUP_NAME");

        var update = d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.Equal(new[] { "DUP_NAME" }, update.Excluded);
        Assert.True(d.Session.Contains("DUP_NAME"));
        Assert.True(d.Session.Contains("dup_name")); // ignoring case
    }

    [Fact]
    public void AnExcludedName_GetsNoMoreVotes_SoItIsNeverConfirmed()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("DUP_NAME");
        d.Next(Pair(Top(1000)), Pair(Top(1400)));

        for (var i = 0; i < 5; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.All(d.Tracker.Tracks, t =>
        {
            Assert.Null(t.ConfirmedName);
            Assert.Equal(TrackVerdict.Nameplate, t.Verdict); // still a nameplate, it just has no name
        });
        Assert.Contains(d.Tracker.Tracks, t => t.DuplicateExcluded);
    }

    [Fact]
    public void ThePartyMemberShowsAsABlueAndAGreenPlate_AndIsNeverExcluded()
    {
        var d = new Driver();
        d.Evaluate = top => top.X < 1200 ? Read("PARTY_MATE", PlateColor.Blue) : Read("PARTY_MATE", PlateColor.Green);

        for (var i = 0; i < 4; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.False(d.Session.Contains("PARTY_MATE"));
        Assert.All(d.Tracker.Tracks, t => Assert.Equal("PARTY_MATE", t.ConfirmedName));
    }

    [Theory]
    [InlineData(PlateColor.White, PlateColor.Blue)]
    [InlineData(PlateColor.White, PlateColor.Red)]
    [InlineData(PlateColor.Red, PlateColor.Red)]
    [InlineData(PlateColor.Green, PlateColor.Green)]
    public void OnlyTwoWhitePlatesCount(PlateColor first, PlateColor second)
    {
        var d = new Driver();
        d.Evaluate = top => Read("SOME_NAME", top.X < 1200 ? first : second);

        for (var i = 0; i < 4; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.False(d.Session.Contains("SOME_NAME"));
    }

    [Fact]
    public void APlateWithoutAMeasuredColor_IsNotCounted()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("NO_COLOR", null);

        for (var i = 0; i < 4; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.False(d.Session.Contains("NO_COLOR"));
    }

    [Fact]
    public void ANameOnAList_IsNeverExcluded_AndIsConfirmedAsUsual()
    {
        var d = new Driver();
        d.IsListed = name => name == "LISTED_ONE";
        d.Evaluate = _ => Read("LISTED_ONE");

        for (var i = 0; i < 4; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.Equal(0, d.Session.Count);
        Assert.All(d.Tracker.Tracks, t => Assert.Equal("LISTED_ONE", t.ConfirmedName));
    }

    [Fact]
    public void ANameThatWasExcludedAndIsThenPutOnAList_IsShownAgain()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("DUP_NAME");
        d.Next(Pair(Top(1000)), Pair(Top(1400)));
        Assert.True(d.Session.Contains("DUP_NAME"));

        d.IsListed = name => name == "DUP_NAME"; // the user adds the player to the list
        for (var i = 0; i < 3; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.All(d.Tracker.Tracks, t => Assert.Equal("DUP_NAME", t.ConfirmedName)); // the list wins: the order is lists first, then the duplicate rule
    }

    [Fact]
    public void OverlappingNameLines_AreTheSamePlateTrackedTwice_NotADuplicate()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("ONE_PLATE");

        for (var i = 0; i < 4; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1030))); // two candidates 30 px apart: the tracks' name lines overlap

        Assert.False(d.Session.Contains("ONE_PLATE"));
    }

    [Fact]
    public void BothTracksMustHaveReadTheNameWithinThreeUpdates()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("OLD_PLATE_NAME");
        d.Next(Pair(Top(1000)));
        d.Next(Pair(Top(1000)));
        for (var i = 0; i < 4; i++)
            d.Next(); // the first plate is gone for four updates (it is still a live track, it expires after a second)

        d.Next(Pair(Top(1400))); // the same name shows up elsewhere: the first track read it 6 updates ago

        Assert.False(d.Session.Contains("OLD_PLATE_NAME"));
    }

    [Fact]
    public void ANameReadByBothTracksOnTheLastThreeUpdates_Counts()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("LATE_PLATE_NAME");
        d.Next(Pair(Top(1000)));
        d.Next(Pair(Top(1000)));
        d.Next(); // the first plate is missing for one update
        d.Next(Pair(Top(1000)));

        var update = d.Next(Pair(Top(1000)), Pair(Top(1400))); // the second plate appears: both read it in this update

        Assert.Contains("LATE_PLATE_NAME", update.Excluded);
    }

    [Fact]
    public void TheSessionList_SurvivesANewTracker()
    {
        var session = new SessionExclusions();
        var first = new Driver(session);
        first.Evaluate = _ => Read("DUP_NAME");
        first.Next(Pair(Top(1000)), Pair(Top(1400)));

        // the live engine makes a new tracker when the game window loses the focus (Reset): the list stays
        var second = new Driver(session);
        second.Evaluate = _ => Read("DUP_NAME");
        for (var i = 0; i < 5; i++)
            second.Next(Pair(Top(1000)));

        Assert.True(session.Contains("DUP_NAME"));
        Assert.Null(Assert.Single(second.Tracker.Tracks).ConfirmedName);
    }

    [Fact]
    public void ANameIsLoggedOnce_WhenTheRuleKeepsFiring()
    {
        var d = new Driver();
        d.Evaluate = _ => Read("DUP_NAME");

        var first = d.Next(Pair(Top(1000)), Pair(Top(1400)));
        var later = Enumerable.Range(0, 4).SelectMany(_ => d.Next(Pair(Top(1000)), Pair(Top(1400))).Excluded).ToList();

        Assert.Single(first.Excluded);
        Assert.Empty(later);
    }

    [Fact]
    public void TheRuleCanBeSwitchedOff()
    {
        var d = new Driver(settings: Settings with { DuplicateRule = false });
        d.Evaluate = _ => Read("DUP_NAME");

        for (var i = 0; i < 4; i++)
            d.Next(Pair(Top(1000)), Pair(Top(1400)));

        Assert.Equal(0, d.Session.Count);
    }

    [Fact]
    public void ATrackFollowsItsRowsAndCarriesTheColor()
    {
        var d = new Driver();
        var rows = new[] { new Rect(990, 462, 100, 12), new Rect(1000, 481, 80, 12) };
        var moved = new[] { new Rect(1000, 462, 100, 12), new Rect(1010, 481, 80, 12) };
        d.Evaluate = top => new TrackEvaluation(true, "ROWS_NAME", top.X == 1000 ? rows : moved, PlateColor.Blue);
        d.Next(new TrackCandidate(rows[1], new Rect(1010, 505, 60, 12), true, rows));

        d.Next(new TrackCandidate(moved[1], new Rect(1020, 505, 60, 12), true, moved));

        var track = Assert.Single(d.Tracker.Tracks);
        Assert.Equal(PlateColor.Blue, track.Color);
        Assert.Equal(moved, track.Rows);
        Assert.Equal(new Rect(1000, 462, 100, 55), track.PlateBox); // the rows and the line below them
    }
}
