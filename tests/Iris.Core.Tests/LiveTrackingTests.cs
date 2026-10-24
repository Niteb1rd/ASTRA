using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Social;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The pure logic of the live overlay (no images, no threads, no timers): the scan geometry, the prediction between reads, the snapshot built from tracks,
/// the window fit and the log summary. Time is always a number given by the test.
/// </summary>
public class LiveTrackingTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ----- ScanGeometry -----

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1280, 1440)]
    [InlineData(1000, 700)]
    public void ScanGeometry_CaptureHoldsTheRegionAndStaysInsideTheWindow(int width, int height)
    {
        var s = NameplateDetectorSettings.Default;
        Assert.True(ScanGeometry.TryCreate(width, height, s, out var g));

        var x0 = (int)(s.RegionLeft * width);
        var x1 = Math.Min(width, (int)(s.RegionRight * width));
        var y0 = (int)(s.RegionTop * height);
        var y1 = Math.Min(height, (int)(s.RegionBottom * height));
        var c = g!.Capture;

        Assert.True(c.X >= 0 && c.Y >= 0 && c.X + c.Width <= width && c.Y + c.Height <= height);
        Assert.True(c.X <= x0 && c.Y <= y0 && c.X + c.Width >= x1 && c.Y + c.Height >= y1);
        Assert.True(x0 - c.X >= Math.Min(x0, s.ContextPadding(height)));        // the context beside the region is there
        Assert.True(y0 - c.Y >= Math.Min(y0, s.VerticalContextPadding(height))); // and above and below it
        Assert.True(c.Y + c.Height - y1 >= Math.Min(height - y1, s.VerticalContextPadding(height)));
        Assert.True(c.Width * c.Height < (long)width * height);                 // it is a part of the screen, not the screen
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1280, 1440)]
    [InlineData(1366, 768)]
    [InlineData(1001, 757)]
    public void ScanGeometry_TheDetectorsTruncationGivesBackTheNominalRegion(int width, int height)
    {
        var s = NameplateDetectorSettings.Default;
        Assert.True(ScanGeometry.TryCreate(width, height, s, out var g));
        var d = g!.DetectorSettings;
        var c = g.Capture;

        // the arithmetic of NameplateDetector.FindCandidates, on the crop
        var x0 = (int)(d.RegionLeft * c.Width);
        var x1 = Math.Min(c.Width, (int)(d.RegionRight * c.Width));
        var y0 = (int)(d.RegionTop * c.Height);
        var y1 = Math.Min(c.Height, (int)(d.RegionBottom * c.Height));

        Assert.Equal((int)(s.RegionLeft * width), x0 + c.X);
        Assert.Equal(Math.Min(width, (int)(s.RegionRight * width)), x1 + c.X);
        Assert.Equal((int)(s.RegionTop * height), y0 + c.Y);
        Assert.Equal(Math.Min(height, (int)(s.RegionBottom * height)), y1 + c.Y);
        Assert.Equal(height, d.ScaleHeight);
    }

    [Fact]
    public void ScanGeometry_TooSmallOrBrokenGivesNothing()
    {
        Assert.False(ScanGeometry.TryCreate(0, 0, NameplateDetectorSettings.Default, out var none));
        Assert.Null(none);
        Assert.False(ScanGeometry.TryCreate(1, 1, NameplateDetectorSettings.Default, out _));
        Assert.False(ScanGeometry.TryCreate(1920, 1080, NameplateDetectorSettings.Default with { RegionLeft = 0.9, RegionRight = 0.1 }, out _));
    }

    [Fact]
    public void ScanGeometry_ToScreenAndCropToWindow_AddTheOffsets()
    {
        Assert.True(ScanGeometry.TryCreate(1920, 1080, NameplateDetectorSettings.Default, out var g));
        var screen = g!.ToScreen(100, 50);
        Assert.Equal(g.Capture.X + 100, screen.X);
        Assert.Equal(g.Capture.Y + 50, screen.Y);
        Assert.Equal(g.Capture.Width, screen.Width);
        var inWindow = g.CropToWindow(new Rect(10, 20, 30, 40));
        Assert.Equal(new Rect(10 + g.Capture.X, 20 + g.Capture.Y, 30, 40), inWindow);
        Assert.True(g.Matches(1920, 1080));
        Assert.False(g.Matches(1920, 1081));
    }

    // ----- LivePredictor -----

    private static readonly LiveSettings Live = LiveSettings.Default;

    private static LiveBracket Bracket(double vx = 0, double vy = 0, double x = 100, double y = 200, double height = 28, double reference = 1000)
        => new(1, "SYNTH_ALPHA", new PixelRect(x, y, 80, height), vx, vy, reference, MatchKind.NoMatch, OverlayColor.Blue);

    private static LiveSnapshot Snapshot(double builtMs, params LiveBracket[] brackets)
        => new(brackets, new[] { new PanelEntry("SYNTH_ALPHA", MatchKind.NoMatch, OverlayColor.Blue, PanelText.NotLookedUp) }, builtMs);

    [Fact]
    public void Predictor_AStationaryBracketStaysAndTheModelInstanceIsKept()
    {
        var snapshot = Snapshot(1000, Bracket());
        var first = LivePredictor.ModelAt(snapshot, 1000, Live);
        var second = LivePredictor.ModelAt(snapshot, 1100, Live, first);

        Assert.Equal(new PixelRect(100, 200, 80, 28), Assert.Single(first.Brackets).Box);
        Assert.Same(first, second); // nothing moved: the window skips the redraw
        Assert.Same(snapshot.Panel, first.Panel);
    }

    [Fact]
    public void Predictor_ABracketMovesWithItsVelocity()
    {
        var snapshot = Snapshot(1000, Bracket(vx: 0.1, vy: -0.05));
        var model = LivePredictor.ModelAt(snapshot, 1200, Live);

        var box = Assert.Single(model.Brackets).Box;
        Assert.Equal(120, box.X);  // 100 + 0.1 * 200
        Assert.Equal(190, box.Y);  // 200 - 0.05 * 200
        Assert.Equal(80, box.Width);
        Assert.Equal(28, box.Height);
    }

    [Fact]
    public void Predictor_MovementUnderAWholePixelKeepsThePreviousModel()
    {
        var snapshot = Snapshot(1000, Bracket(vx: 0.1));
        var previous = LivePredictor.ModelAt(snapshot, 1000, Live);
        var next = LivePredictor.ModelAt(snapshot, 1004, Live, previous); // 0.4 px

        Assert.Same(previous, next);
        var moved = LivePredictor.ModelAt(snapshot, 1100, Live, previous); // 10 px
        Assert.NotSame(previous, moved);
        Assert.Equal(110, moved.Brackets[0].Box.X);
        Assert.Same(previous.Panel, moved.Panel);
    }

    [Fact]
    public void Predictor_ASlowTrackIsTakenAsStandingStill()
    {
        var snapshot = Snapshot(1000, Bracket(vx: 0.005)); // 5 px a second: jitter, not motion
        var model = LivePredictor.ModelAt(snapshot, 1400, Live);

        Assert.Equal(100, model.Brackets[0].Box.X);
    }

    [Fact]
    public void Predictor_PredictionStopsAfterTheMaximumTime()
    {
        var settings = Live with { StaleHideMs = 100_000 };
        var snapshot = Snapshot(1000, Bracket(vx: 0.1));

        var at500 = LivePredictor.ModelAt(snapshot, 1500, settings).Brackets[0].Box.X;
        var at2000 = LivePredictor.ModelAt(snapshot, 3000, settings).Brackets[0].Box.X;

        Assert.Equal(150, at500);
        Assert.Equal(150, at2000); // 500 ms is as far as it goes
    }

    [Fact]
    public void Predictor_TheShiftIsCappedAtAFewBoxHeights()
    {
        var snapshot = Snapshot(1000, Bracket(vx: 3.0, height: 20)); // a "jump" velocity: 3 px/ms
        var shift = LivePredictor.Shift(snapshot.Brackets[0], 1400, Live);

        Assert.Equal(60, shift.X, 6); // 3 box heights of 20 px
        Assert.Equal(0, shift.Y, 6);
    }

    [Fact]
    public void Predictor_ShiftIsZeroBeforeTheReferenceTime()
    {
        var shift = LivePredictor.Shift(Bracket(vx: 0.5, reference: 2000), 1500, Live);
        Assert.Equal((0.0, 0.0), shift);
    }

    [Fact]
    public void Predictor_AStaleSnapshotOrAnEmptyOneGivesTheEmptyModel()
    {
        var snapshot = Snapshot(1000, Bracket());

        Assert.Same(OverlayModel.Empty, LivePredictor.ModelAt(snapshot, 1000 + Live.StaleHideMs + 1, Live));
        Assert.NotSame(OverlayModel.Empty, LivePredictor.ModelAt(snapshot, 1000 + Live.StaleHideMs, Live));
        Assert.Same(OverlayModel.Empty, LivePredictor.ModelAt(LiveSnapshot.Empty, 0, Live));
    }

    [Fact]
    public void Predictor_ACorrectionChangesTheModel()
    {
        var before = Snapshot(1000, Bracket());
        var model = LivePredictor.ModelAt(before, 1000, Live);
        var corrected = new LiveSnapshot(
            new[] { Bracket() with { Kind = MatchKind.PlayerList, Color = OverlayColor.Cyan } }, before.Panel, 1100);

        var next = LivePredictor.ModelAt(corrected, 1100, Live, model);

        Assert.NotSame(model, next);
        Assert.Equal(MatchKind.PlayerList, next.Brackets[0].Kind);
        Assert.Equal(OverlayColor.Cyan, next.Brackets[0].Color);
    }

    // ----- LiveSettings -----

    [Fact]
    public void LiveSettings_DefaultsAreTheSpecifiedOnes()
    {
        Assert.Equal(5.0, Live.DetectionRateHz);
        Assert.Equal(200.0, Live.DetectionPeriodMs);
        Assert.Null(Live.Validate());
        Assert.NotNull((Live with { DetectionRateHz = 0 }).Validate());
        Assert.NotNull((Live with { StaleHideMs = -1 }).Validate());
    }

    // ----- LiveStats -----

    [Fact]
    public void Percentile95_IsTheNearestRank()
    {
        Assert.Equal(0.0, LiveStats.Percentile95(Array.Empty<double>()));
        Assert.Equal(7.0, LiveStats.Percentile95(new[] { 7.0 }));
        Assert.Equal(19.0, LiveStats.Percentile95(Enumerable.Range(1, 20).Select(i => (double)i).ToList()));
        Assert.Equal(40.0, LiveStats.Percentile95(new[] { 40.0, 10.0, 30.0, 20.0 }));
    }

    [Fact]
    public void Stats_SummaryHasTheNumbersAndStartsANewWindow()
    {
        var stats = new LiveStats(0);
        foreach (var ms in new[] { 10.0, 20.0, 30.0, 40.0 })
            stats.AddRead(ms);
        stats.AddSkippedSlots(2);
        stats.AddSkippedSlots(-5); // ignored
        stats.AddUnavailable();

        Assert.False(stats.IsDue(59_999, 60_000));
        Assert.True(stats.IsDue(60_000, 60_000));
        var line = stats.TakeSummary(60_000, 3);

        Assert.Contains("4 reads in 60 s", line);
        Assert.Contains("average 25.0 ms", line);
        Assert.Contains("p95 40.0 ms", line);
        Assert.Contains("2 slots skipped", line);
        Assert.Contains("1 without a picture", line);
        Assert.Contains("3 confirmed tracks", line);
        Assert.Equal(0, stats.Reads);
        Assert.Equal(0, stats.SkippedSlots);
        Assert.Equal(0, stats.Unavailable);
        Assert.False(stats.IsDue(90_000, 60_000));
        Assert.True(stats.IsDue(120_000, 60_000));
        Assert.Contains("0 reads in 60 s, average 0.0 ms", stats.TakeSummary(120_000, 0));
    }

    // ----- OverlayWindowFit and BracketMetrics -----

    [Fact]
    public void Fit_UnionIgnoresEmptyRectangles()
    {
        Assert.Null(OverlayWindowFit.Union(Array.Empty<PixelRect>()));
        Assert.Null(OverlayWindowFit.Union(new[] { default(PixelRect) }));
        var union = OverlayWindowFit.Union(new[] { new PixelRect(10, 10, 20, 20), default, new PixelRect(100, 50, 10, 10) });
        Assert.Equal(new PixelRect(10, 10, 100, 50), union);
    }

    [Fact]
    public void Fit_ClipAndContains()
    {
        var bounds = new PixelRect(0, 0, 1000, 500);
        Assert.Equal(new PixelRect(900, 450, 100, 50), OverlayWindowFit.Clip(new PixelRect(900, 450, 300, 300), bounds));
        Assert.True(OverlayWindowFit.Clip(new PixelRect(2000, 0, 10, 10), bounds).IsEmpty);
        Assert.True(OverlayWindowFit.Contains(bounds, new PixelRect(10, 10, 10, 10)));
        Assert.False(OverlayWindowFit.Contains(bounds, new PixelRect(995, 10, 10, 10)));
    }

    [Fact]
    public void Fit_ChooseGrowsByTheMarginAndClipsToTheBounds()
    {
        var bounds = new PixelRect(0, 0, 2560, 1440);
        var chosen = OverlayWindowFit.Choose(null, new PixelRect(100, 100, 200, 50), bounds, 64);
        Assert.Equal(new PixelRect(36, 36, 328, 178), chosen);

        var atEdge = OverlayWindowFit.Choose(null, new PixelRect(10, 10, 200, 50), bounds, 64);
        Assert.Equal(0, atEdge.X);
        Assert.Equal(0, atEdge.Y);
    }

    [Fact]
    public void Fit_ChooseKeepsTheCurrentWindowWhileItStillFits()
    {
        var bounds = new PixelRect(0, 0, 2560, 1440);
        var current = OverlayWindowFit.Choose(null, new PixelRect(100, 100, 200, 50), bounds, 64);

        var moved = OverlayWindowFit.Choose(current, new PixelRect(120, 110, 200, 50), bounds, 64); // a few pixels on

        Assert.Equal(current, moved);
    }

    [Fact]
    public void Fit_ChooseGrowsWhenTheContentLeavesTheWindowAndShrinksWhenItIsMuchTooBig()
    {
        var bounds = new PixelRect(0, 0, 2560, 1440);
        var current = OverlayWindowFit.Choose(null, new PixelRect(100, 100, 200, 50), bounds, 64);

        var outside = OverlayWindowFit.Choose(current, new PixelRect(900, 600, 200, 50), bounds, 64);
        Assert.NotEqual(current, outside);
        Assert.True(OverlayWindowFit.Contains(outside, new PixelRect(900, 600, 200, 50)));

        var shrunk = OverlayWindowFit.Choose(bounds, new PixelRect(100, 100, 200, 50), bounds, 64);
        Assert.Equal(current, shrunk);

        Assert.True(OverlayWindowFit.Choose(current, new PixelRect(5000, 5000, 10, 10), bounds, 64).IsEmpty);
    }

    [Fact]
    public void Fit_ChooseIgnoresACurrentWindowOutsideTheBounds()
    {
        var bounds = new PixelRect(0, 0, 800, 600);
        var stale = new PixelRect(0, 0, 2560, 1440); // from a bigger game window
        var chosen = OverlayWindowFit.Choose(stale, new PixelRect(100, 100, 200, 50), bounds, 64);
        Assert.True(OverlayWindowFit.Contains(bounds, chosen));
    }

    [Fact]
    public void BracketMetrics_ExtentHoldsBothBracketsWithTheirOutline()
    {
        var box = new PixelRect(100, 100, 80, 20);
        var m = BracketMetrics.For(box);
        var extent = m.Extent(box);

        Assert.Equal(97.6, m.Top, 9); // 12 % of the box height above the box
        Assert.Equal(24.8, m.Height, 9);
        Assert.Equal(88.448, extent.X, 9);
        Assert.Equal(95.6, extent.Y, 9);
        Assert.Equal(191.552, extent.Right, 9);
        Assert.Equal(28.8, extent.Height, 9);
        Assert.True(OverlayWindowFit.Contains(extent, box));
        Assert.Equal(extent, BracketMetrics.ExtentOf(box));
    }

    // ----- LiveSnapshotBuilder over a real tracker (fake evaluations) -----

    private const double Step = 100.0;

    private sealed class Rig
    {
        private readonly NameplateTracker _tracker;
        private long _frame;
        private string? _name;

        public Rig(ExclusionList exclusions, NameplateTrackerSettings? tracker = null, Func<string, IReadOnlyList<OrgMembership>?>? orgs = null)
        {
            Exclusions = exclusions;
            _tracker = new NameplateTracker(tracker ?? FastTracker);
            Builder = new LiveSnapshotBuilder(exclusions, orgs ?? (_ => null));
        }

        public ExclusionList Exclusions { get; }
        public LiveSnapshotBuilder Builder { get; }
        public SocialConfig Config { get; } = new();
        public double Now { get; private set; } = 1000.0;
        public LiveSnapshot Snapshot { get; private set; } = LiveSnapshot.Empty;
        public NameplateTracker Tracker => _tracker;

        /// <summary>One read at the next time step with one plate (name read as <paramref name="name"/>, top line at x), or none (null rect).</summary>
        public LiveSnapshot Next(string? name, int x = 1000, bool plate = true)
        {
            _name = name;
            var top = new Rect(x, 500, 80, 12);
            var candidates = plate ? new[] { new TrackCandidate(top, new Rect(top.X + 10, top.Y + 24, top.Width - 20, top.Height)) } : Array.Empty<TrackCandidate>();
            _tracker.Update(_frame++, Now, candidates, (_, _) => new TrackEvaluation(_name is not null, _name));
            Snapshot = Builder.Build(_tracker.Tracks, Config, 100, 50, Now, Snapshot);
            Now += Step;
            return Snapshot;
        }

        /// <summary>The time of the last read.</summary>
        public double Last => Now - Step;
    }

    private static readonly NameplateTrackerSettings FastTracker = NameplateTrackerSettings.Default with { ConfirmReads = 2, ConfirmSpanMs = 0, ConfirmedRereadMs = 0 };

    private Rig NewRig(NameplateTrackerSettings? tracker = null, Func<string, IReadOnlyList<OrgMembership>?>? orgs = null)
        => new(ExclusionList.LoadOrCreate(_dir.File("exclusions.txt"), new FakeClock(), new ListLog()), tracker, orgs);

    [Fact]
    public void Builder_AConfirmedNameplateGetsABracketInWindowPixelsAndAPanelEntry()
    {
        var rig = NewRig();
        var first = rig.Next("SYNTH_ALPHA");
        Assert.Empty(first.Brackets); // read once: not confirmed

        var second = rig.Next("SYNTH_ALPHA");

        var bracket = Assert.Single(second.Brackets);
        Assert.Equal("SYNTH_ALPHA", bracket.Handle);
        Assert.Equal(MatchKind.NoMatch, bracket.Kind);
        Assert.Equal(OverlayColor.Blue, bracket.Color);
        // name line (1000,500,80,12) and distance line (1010,524,60,12): their union, plus the window offset (100,50)
        Assert.Equal(new PixelRect(1100, 550, 80, 36), bracket.Box);
        var entry = Assert.Single(second.Panel);
        Assert.Equal("SYNTH_ALPHA", entry.Handle);
        Assert.Equal(PanelText.NotLookedUp, entry.OrgsLine);
        Assert.Equal(rig.Last, second.BuiltMs);
    }

    [Fact]
    public void Builder_AMovingTrackCarriesItsVelocityAndReferenceTime()
    {
        var rig = NewRig();
        rig.Next("SYNTH_ALPHA", x: 1000);
        var snapshot = rig.Next("SYNTH_ALPHA", x: 1010);

        var bracket = Assert.Single(snapshot.Brackets);
        Assert.Equal(0.1, bracket.VxPerMs, 6); // 10 px in 100 ms
        Assert.Equal(0.0, bracket.VyPerMs, 6);
        Assert.Equal(rig.Last, bracket.ReferenceMs);
    }

    [Fact]
    public void Builder_APlayerListNameHasThePlayerListShape()
    {
        var rig = NewRig();
        rig.Config.Players.Add(new PlayerEntry("SYNTH_ALPHA", OverlayColor.Cyan));
        rig.Next("SYNTH_ALPHA");
        var snapshot = rig.Next("SYNTH_ALPHA");

        var bracket = Assert.Single(snapshot.Brackets);
        Assert.Equal(MatchKind.PlayerList, bracket.Kind);
        Assert.Equal(OverlayColor.Cyan, bracket.Color);
    }

    [Fact]
    public void Builder_AnOrgListMemberWithKnownOrgsGetsTheOrgListShape()
    {
        var rig = NewRig(orgs: handle => new[] { new OrgMembership("HEKATE", true) });
        rig.Config.Orgs.Add(new OrgEntry("HEKATE", 1, OverlayColor.Red));
        rig.Next("SYNTH_ALPHA");
        var snapshot = rig.Next("SYNTH_ALPHA");

        var bracket = Assert.Single(snapshot.Brackets);
        Assert.Equal(MatchKind.OrgList, bracket.Kind);
        Assert.Equal(OverlayColor.Red, bracket.Color);
        Assert.NotEqual(PanelText.NotLookedUp, Assert.Single(snapshot.Panel).OrgsLine);
    }

    [Fact]
    public void Builder_ACorrectionChangesTheKindAtOnce()
    {
        var rig = NewRig();
        rig.Config.Players.Add(new PlayerEntry("BBBB", OverlayColor.Cyan));
        rig.Next("AAAA");
        var confirmed = rig.Next("AAAA");
        Assert.Equal(MatchKind.NoMatch, Assert.Single(confirmed.Brackets).Kind);

        rig.Next("BBBB");
        rig.Next("BBBB");
        var corrected = rig.Next("BBBB"); // B:3 beats A:2

        var bracket = Assert.Single(corrected.Brackets);
        Assert.Equal("BBBB", bracket.Handle);
        Assert.Equal(MatchKind.PlayerList, bracket.Kind);
        Assert.Equal("BBBB", Assert.Single(corrected.Panel).Handle);
    }

    [Fact]
    public void Builder_ExcludedNamesAndContactCodesGetNoBracket_ButThePlayerListWins()
    {
        var rig = NewRig();
        Assert.True(rig.Exclusions.AddAutomatic("EXCL_NAME", "test"));
        rig.Next("EXCL_NAME");
        Assert.Empty(rig.Next("EXCL_NAME").Brackets);

        var codeRig = NewRig();
        codeRig.Next("PA-5968-XF");
        Assert.Empty(codeRig.Next("PA-5968-XF").Brackets);

        var winRig = NewRig();
        winRig.Exclusions.AddAutomatic("EXCL_NAME", "test");
        winRig.Config.Players.Add(new PlayerEntry("EXCL_NAME", OverlayColor.Cyan));
        winRig.Next("EXCL_NAME");
        Assert.Single(winRig.Next("EXCL_NAME").Brackets);

        var codeWins = NewRig();
        codeWins.Config.Players.Add(new PlayerEntry("PA-5968-XF", OverlayColor.Cyan));
        codeWins.Next("PA-5968-XF");
        Assert.Single(codeWins.Next("PA-5968-XF").Brackets);
    }

    [Fact]
    public void Builder_ATrackNoReadHasSeenForTheStaleTimeIsHidden_AndAnExpiredTrackIsGone()
    {
        var rig = NewRig(FastTracker with { ExpireMs = 1000 });
        rig.Next("SYNTH_ALPHA");
        var shown = rig.Next("SYNTH_ALPHA");
        Assert.Single(shown.Brackets);
        var lastSeen = rig.Last;

        // the plate is gone from the screen: reads without it
        var s1 = rig.Next(null, plate: false);
        Assert.Single(s1.Brackets); // 100 ms
        rig.Next(null, plate: false);
        rig.Next(null, plate: false);
        var s4 = rig.Next(null, plate: false); // 400 ms
        Assert.Single(s4.Brackets);
        var s7 = Advance(rig, lastSeen + 700); // 700 ms: stale (600), the tracker still holds the track (expires after 1000 ms)
        Assert.Empty(s7.Brackets);
        Assert.Single(rig.Tracker.Tracks);

        var later = Advance(rig, lastSeen + 1100);
        Assert.Empty(later.Brackets);
        Assert.Empty(rig.Tracker.Tracks);
    }

    private static LiveSnapshot Advance(Rig rig, double untilMs)
    {
        LiveSnapshot last = rig.Snapshot;
        while (rig.Now <= untilMs)
            last = rig.Next(null, plate: false);
        return last;
    }

    [Fact]
    public void Builder_ThePanelKeepsItsInstanceWhileItsContentIsTheSame()
    {
        var rig = NewRig();
        rig.Next("SYNTH_ALPHA");
        var a = rig.Next("SYNTH_ALPHA");
        var b = rig.Next("SYNTH_ALPHA", x: 1004);

        Assert.NotEmpty(a.Panel);
        Assert.Same(a.Panel, b.Panel);

        rig.Config.Players.Add(new PlayerEntry("SYNTH_ALPHA", OverlayColor.Cyan));
        var c = rig.Next("SYNTH_ALPHA", x: 1004);
        Assert.NotSame(b.Panel, c.Panel);
    }

    [Fact]
    public void Builder_TheSameNameOnTwoPlatesHasTwoBracketsAndOnePanelEntry()
    {
        var rig = NewRig();
        // two tracks, far apart, both read as the same name
        var tracker = rig.Tracker;
        long frame = 0;
        LiveSnapshot snapshot = LiveSnapshot.Empty;
        for (var i = 0; i < 2; i++)
        {
            var a = new Rect(1000, 500, 80, 12);
            var b = new Rect(1600, 800, 80, 12);
            tracker.Update(frame++, 1000 + i * Step, new[]
            {
                new TrackCandidate(a, new Rect(1010, 524, 60, 12)),
                new TrackCandidate(b, new Rect(1610, 824, 60, 12)),
            }, (_, _) => new TrackEvaluation(true, "SYNTH_ALPHA"));
            snapshot = rig.Builder.Build(tracker.Tracks, rig.Config, 0, 0, 1000 + i * Step, snapshot);
        }

        Assert.Equal(2, snapshot.Brackets.Count);
        Assert.Single(snapshot.Panel);
    }

    [Fact]
    public void Builder_NothingConfirmedGivesAnEmptySnapshotWithTheTime()
    {
        var rig = NewRig();
        var snapshot = rig.Next(null);
        Assert.Empty(snapshot.Brackets);
        Assert.Empty(snapshot.Panel);
        Assert.Equal(1000, snapshot.BuiltMs);
    }

    [Fact]
    public void TrackMotion_IsZeroForATrackSeenOnce_AndFollowsTheLastTwoAssociations()
    {
        var tracker = new NameplateTracker(FastTracker);
        var top = new Rect(1000, 500, 80, 12);
        tracker.Update(0, 1000, new[] { new TrackCandidate(top, new Rect(1010, 524, 60, 12)) }, (_, _) => TrackEvaluation.No);
        var once = tracker.Tracks[0].Motion(500);
        Assert.Equal(0.0, once.VxPerMs);
        Assert.Equal(0.0, once.VyPerMs);
        Assert.Equal(1000.0, once.AtMs);

        top = new Rect(1008, 504, 80, 12);
        tracker.Update(1, 1100, new[] { new TrackCandidate(top, new Rect(1018, 528, 60, 12)) }, (_, _) => TrackEvaluation.No);
        var twice = tracker.Tracks[0].Motion(500);
        Assert.Equal(0.08, twice.VxPerMs, 6);
        Assert.Equal(0.04, twice.VyPerMs, 6);
        Assert.Equal(1100.0, twice.AtMs);

        Assert.Equal(0.0, tracker.Tracks[0].Motion(50).VxPerMs); // the two associations are further apart than the cap
    }
}
