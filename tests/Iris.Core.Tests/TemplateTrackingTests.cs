using Iris.Core.Config;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Social;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// Detect, then track: the template tracker follows a plate in synthetic pictures (white text on a dark window), loses it when it vanishes, and a detection wins;
/// the seeds, the hand-over between the capture loop and the detection thread, the published-bracket history and the statistics.
/// </summary>
public class TemplateTrackingTests
{
    private const int W = 960;
    private const int H = 1440;
    private const string Font = "SYNTH_ALPHA";

    private static readonly LiveSettings Live = LiveSettings.Default;

    // ----- the synthetic plate -----

    private static Mat Window() => new(H, W, MatType.CV_8UC3, new Scalar(25, 25, 25));

    private static Size TextSize(out int baseline) => Cv2.GetTextSize(Font, HersheyFonts.HersheySimplex, 1.2, 2, out baseline);

    private static PixelRect NameBoxAt(int x, int y)
    {
        var size = TextSize(out var baseline);
        return new PixelRect(x, y, size.Width, size.Height + baseline);
    }

    /// <summary>The whole plate: the name row and a distance line (22 px) under it.</summary>
    private static PixelRect PlateBoxAt(int x, int y)
    {
        var name = NameBoxAt(x, y);
        return new PixelRect(x, y, name.Width, name.Height + 22);
    }

    private static void DrawPlate(Mat window, int x, int y, Scalar color)
    {
        var size = TextSize(out _);
        Cv2.PutText(window, Font, new Point(x, y + size.Height), HersheyFonts.HersheySimplex, 1.2, color, 2);
    }

    private static readonly Scalar White = new(255, 255, 255);

    private static Rect ToRect(PixelRect box) => new((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);

    private static DetectionResult Detection(Mat window, int x, int y, double atMs, double vx = 0, double vy = 0, int trackId = 1)
    {
        using var builder = new PlateSeedBuilder();
        var name = NameBoxAt(x, y);
        var template = builder.MakeTemplate(window, ToRect(name), H, out var cls);
        var plate = PlateBoxAt(x, y);
        var seed = new PlateSeed(trackId, plate, name, template, cls, vx, vy, atMs);
        var bracket = new LiveBracket(trackId, "SYNTH_ALPHA", plate, vx, vy, atMs, MatchKind.NoMatch, OverlayColor.Blue);
        var snapshot = new LiveSnapshot(new[] { bracket }, new[] { new PanelEntry("SYNTH_ALPHA", MatchKind.NoMatch, OverlayColor.Blue, PanelText.NotLookedUp) }, atMs);
        return new DetectionResult(snapshot, new[] { seed }, atMs, W, H);
    }

    /// <summary>One tracking tick on <paramref name="window"/> at <paramref name="nowMs"/>, the way the capture loop does it.</summary>
    private static TrackTick Tick(PlateTemplateTracker tracker, Mat window, double nowMs, double tickMs = 33.0)
    {
        var plan = tracker.Plan(nowMs, tickMs, W, H);
        var patches = new List<Mat?>();
        try
        {
            foreach (var item in plan)
                patches.Add(new Mat(window, item.Patch).Clone());
            return tracker.Update(nowMs, plan, patches);
        }
        finally
        {
            foreach (var patch in patches)
                patch?.Dispose();
        }
    }

    // ----- following -----

    [OpenCvFact]
    public void Tracker_FollowsAMovingPlateFromTickToTick()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, White);
            tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        }

        for (var i = 1; i <= 30; i++)
        {
            var now = 1000 + 33.0 * i;
            var x = 200 + 6 * i;  // about 180 px/s to the right
            var y = 300 + 2 * i;  // and down
            using var frame = Window();
            DrawPlate(frame, x, y, White);

            var tick = Tick(tracker, frame, now);

            Assert.Equal(1, tick.Attempts);
            Assert.Equal(1, tick.Found);
            var bracket = Assert.Single(tracker.Snapshot(now).Brackets);
            Assert.InRange(bracket.Box.X, x - 1.0, x + 1.0);
            Assert.InRange(bracket.Box.Y, y - 1.0, y + 1.0);
            Assert.Equal(now, bracket.ReferenceMs);
            if (i >= 4)
            {
                Assert.InRange(bracket.VxPerMs, 6.0 / 33.0 - 0.03, 6.0 / 33.0 + 0.03);
                Assert.InRange(bracket.VyPerMs, 2.0 / 33.0 - 0.03, 2.0 / 33.0 + 0.03);
            }
        }
    }

    [OpenCvFact]
    public void Tracker_AFastPlateWithinTheSearchMarginIsStillFound()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, White);
            tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        }

        // 50 px in 33 ms is 1500 px/s, under the 2000 px/s limit (margin 66 px at 1440 for a 33 ms tick)
        using var moved = Window();
        DrawPlate(moved, 250, 300, White);
        var tick = Tick(tracker, moved, 1033);

        Assert.Equal(1, tick.Found);
        Assert.InRange(Assert.Single(tracker.Snapshot(1033).Brackets).Box.X, 249.0, 251.0);
    }

    [OpenCvFact]
    public void Tracker_ABelowThresholdMatchKeepsThePredictionAndLosesThePlateAfter300Ms()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, White);
            tracker.ApplyDetection(Detection(first, 200, 300, 1000, vx: 0.1), 1000);
        }

        using var empty = Window(); // the plate is gone
        var t = 1000.0;
        var lastFound = 1000.0;

        // not found: the bracket goes on along the velocity (prediction), 33 ms per tick
        t += 33;
        var tick = Tick(tracker, empty, t);
        Assert.Equal(1, tick.Attempts);
        Assert.Equal(0, tick.Found);
        var bracket = Assert.Single(tracker.Snapshot(t).Brackets);
        Assert.InRange(bracket.Box.X, 200 + 3.3 - 0.01, 200 + 3.3 + 0.01);

        // up to 300 ms after the last time it was seen it is still shown ...
        while (t + 33 - lastFound <= 300)
        {
            t += 33;
            Tick(tracker, empty, t);
            Assert.Single(tracker.Snapshot(t).Brackets);
        }

        // ... and the next tick is past 300 ms: the bracket is hidden
        t += 33;
        Tick(tracker, empty, t);
        Assert.True(t - lastFound > Live.TrackLostMs);
        Assert.Empty(tracker.Snapshot(t).Brackets);
        Assert.Empty(tracker.Plan(t, 33, W, H)); // and nothing is searched for it any more
    }

    [OpenCvFact]
    public void Tracker_ALostPlateIsShownAgainByTheNextDetection()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, White);
            tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        }

        using var empty = Window();
        for (var t = 1033.0; t <= 1400; t += 33)
            Tick(tracker, empty, t);
        Assert.Empty(tracker.Snapshot(1400).Brackets);

        using var later = Window();
        DrawPlate(later, 420, 380, White);
        tracker.ApplyDetection(Detection(later, 420, 380, 1450), 1450);

        var bracket = Assert.Single(tracker.Snapshot(1450).Brackets);
        Assert.Equal(420, bracket.Box.X);
        Assert.Equal(380, bracket.Box.Y);
        Assert.Single(tracker.Plan(1450, 33, W, H));
    }

    [OpenCvFact]
    public void Tracker_ADetectionAlwaysWinsOverTracking()
    {
        using var tracker = new PlateTemplateTracker();
        using var first = Window();
        DrawPlate(first, 200, 300, White);
        tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        for (var i = 1; i <= 5; i++)
        {
            using var frame = Window();
            DrawPlate(frame, 200 + 4 * i, 300, White);
            Tick(tracker, frame, 1000 + 33.0 * i);
        }

        Assert.InRange(Assert.Single(tracker.Snapshot(1165).Brackets).Box.X, 219.0, 221.0);

        // the detection puts the plate somewhere else, with another velocity: position, velocity and time are the detection's
        using var elsewhere = Window();
        DrawPlate(elsewhere, 500, 700, White);
        tracker.ApplyDetection(Detection(elsewhere, 500, 700, 1200, vx: -0.05, vy: 0.02), 1200);

        var bracket = Assert.Single(tracker.Snapshot(1200).Brackets);
        Assert.Equal(500, bracket.Box.X);
        Assert.Equal(700, bracket.Box.Y);
        Assert.Equal(-0.05, bracket.VxPerMs, 6);
        Assert.Equal(0.02, bracket.VyPerMs, 6);
        Assert.Equal(1200, bracket.ReferenceMs);
        Assert.Equal(1, tracker.TemplateCount);

        // and the next tick looks where the detection put it (prediction 500 - 0.05 * 33)
        var plan = Assert.Single(tracker.Plan(1233, 33, W, H));
        Assert.InRange(plan.PredictedX, 500 - 0.05 * 33 - 0.01, 500 - 0.05 * 33 + 0.01);
    }

    [OpenCvFact]
    public void Tracker_ADetectionThatNoLongerListsAPlateMakesTheTrackerForgetIt()
    {
        using var tracker = new PlateTemplateTracker();
        using var first = Window();
        DrawPlate(first, 200, 300, White);
        tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        Assert.Equal(1, tracker.PlateCount);

        tracker.ApplyDetection(DetectionResult.Empty(1200, W, H), 1200);

        Assert.Equal(0, tracker.PlateCount);
        Assert.Empty(tracker.Snapshot(1200).Brackets);
    }

    [OpenCvFact]
    public void Tracker_AScreenThatDidNotChangeHoldsTheBrackets()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, White);
            tracker.ApplyDetection(Detection(first, 200, 300, 1000, vx: 0.2), 1000);
        }

        // half a second of ticks in which the screen did not change (no new picture): nothing moved, so nothing is lost and nothing drifts
        var attempts = 0;
        for (var t = 1033.0; t <= 1500; t += 33)
        {
            var plan = tracker.Plan(t, 33, W, H);
            attempts += tracker.Update(t, plan, null, screenUnchanged: true).Attempts;
        }

        Assert.Equal(0, attempts);
        var bracket = Assert.Single(tracker.Snapshot(1500).Brackets); // not lost, though the last detection was 500 ms ago
        Assert.Equal(200, bracket.Box.X);
        Assert.Equal(0, bracket.VxPerMs);
    }

    [OpenCvFact]
    public void Tracker_FollowsAtMostTheCappedNumberOfPlates()
    {
        using var tracker = new PlateTemplateTracker();
        var seeds = new List<PlateSeed>();
        var brackets = new List<LiveBracket>();
        for (var id = 1; id <= Live.MaxTrackedPlates + 6; id++)
        {
            var box = new PixelRect(20 + 10 * id, 100, 40, 20);
            var template = new Mat(20, 40, MatType.CV_8UC1, new Scalar(255));
            seeds.Add(new PlateSeed(id, box, box, template, PlateColorClass.Any, 0.05, 0, 1000));
            brackets.Add(new LiveBracket(id, "P" + id, box, 0.05, 0, 1000, MatchKind.NoMatch, OverlayColor.Blue));
        }

        tracker.ApplyDetection(new DetectionResult(new LiveSnapshot(brackets, Array.Empty<PanelEntry>(), 1000), seeds, 1000, W, H), 1000);

        Assert.Equal(24, Live.MaxTrackedPlates);
        Assert.Equal(Live.MaxTrackedPlates, tracker.TemplateCount);
        Assert.Equal(Live.MaxTrackedPlates + 6, tracker.PlateCount); // the others still have a bracket, moved by their velocity
        var plan = tracker.Plan(1033, 33, W, H);
        Assert.Equal(Live.MaxTrackedPlates, plan.Count);

        tracker.Update(1033, plan, null); // no pictures: everybody goes on along the velocity
        var moved = tracker.Snapshot(1033).Brackets.Single(b => b.TrackId == Live.MaxTrackedPlates + 6);
        var start = 20 + 10 * (Live.MaxTrackedPlates + 6);
        Assert.InRange(moved.Box.X, start + 0.05 * 33 - 0.01, start + 0.05 * 33 + 0.01);
        Assert.Equal(1033, moved.ReferenceMs);
    }

    // ----- review fixes: stale prediction base, tracked position wins, hysteresis, held screen, no-template plates -----

    [OpenCvFact]
    public void Tracker_TheMarginGrowsWithTheAgeOfThePredictionBase_UpToTheMaximumPredictionTime()
    {
        using var tracker = new PlateTemplateTracker();

        Assert.Equal(66.6, tracker.Margin(33.3, 1440, 0), 1);       // the tick
        Assert.Equal(400.0, tracker.Margin(33.3, 1440, 200), 1);    // a detection's picture 200 ms old: 2000 px/s x 0.2 s
        Assert.Equal(200.0, tracker.Margin(33.3, 720, 200), 1);     // scaled with the height
        Assert.Equal(1000.0, tracker.Margin(33.3, 1440, 5000), 1);  // capped at MaxPredictionMs (500 ms)
    }

    [OpenCvFact]
    public void Tracker_ThePatchOfAPlateWhosePositionIsOldIsWideEnoughForTheWayItWent()
    {
        using var tracker = new PlateTemplateTracker();
        using var first = Window();
        DrawPlate(first, 450, 500, White);
        tracker.ApplyDetection(Detection(first, 450, 500, 1000), 1000); // the position is as old as the picture

        var fresh = Assert.Single(tracker.Plan(1033, 33, W, H));
        var aged = Assert.Single(tracker.Plan(1250, 33, W, H));

        Assert.True(aged.Patch.Width > fresh.Patch.Width + 100, "250 ms at up to 2000 px/s is 500 px, not 66");
        Assert.True(aged.Patch.X <= fresh.Patch.X - 100);
    }

    [OpenCvFact]
    public void Tracker_ADetectionOlderThanTheTrackingKeepsTheTrackedPositionAndVelocity()
    {
        using var tracker = new PlateTemplateTracker();
        using var first = Window();
        DrawPlate(first, 200, 300, White);
        tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        for (var i = 1; i <= 5; i++)
        {
            using var frame = Window();
            DrawPlate(frame, 200 + 4 * i, 300, White);
            Tick(tracker, frame, 1000 + 33.0 * i);
        }

        // the detection read a picture from 1100 ms (the plate was at 210 then) and arrives now, after the tracking found it at 220 (1165 ms)
        using var older = Window();
        DrawPlate(older, 210, 300, White);
        tracker.ApplyDetection(Detection(older, 210, 300, 1100, vx: 0.0), 1170);

        var bracket = Assert.Single(tracker.Snapshot(1170).Brackets);
        Assert.InRange(bracket.Box.X, 219.0, 221.0);   // not thrown back to 210
        Assert.True(bracket.VxPerMs > 0.05, "the tracked velocity is kept, not the detection's 0");
        Assert.Equal(1165, bracket.ReferenceMs);
        Assert.Equal(1, tracker.TemplateCount);        // the template is the detection's new one
    }

    [OpenCvFact]
    public void Tracker_ADetectionThatAgreesWithinTwoPixelsKeepsTheTrackedBox_ADisagreeingOneWins()
    {
        using var tracker = new PlateTemplateTracker();
        using var first = Window();
        DrawPlate(first, 200, 300, White);
        tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);

        using var near = Window();
        DrawPlate(near, 201, 299, White);
        tracker.ApplyDetection(Detection(near, 201, 299, 1200), 1200);
        var kept = Assert.Single(tracker.Snapshot(1200).Brackets);
        Assert.Equal(200, kept.Box.X); // a still bracket does not hop by a pixel
        Assert.Equal(300, kept.Box.Y);

        using var far = Window();
        DrawPlate(far, 206, 300, White);
        tracker.ApplyDetection(Detection(far, 206, 300, 1400), 1400);
        Assert.Equal(206, Assert.Single(tracker.Snapshot(1400).Brackets).Box.X);
    }

    [OpenCvFact]
    public void Tracker_AnUnchangedScreenDoesNotKeepAPlateThatWasMissingAlive()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, White);
            tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        }

        using var empty = Window();
        Tick(tracker, empty, 1033); // not found: the last attempt failed
        for (var t = 1066.0; t <= 1400; t += 33)
        {
            var plan = tracker.Plan(t, 33, W, H);
            tracker.Update(t, plan, null, screenUnchanged: true);
        }

        Assert.Empty(tracker.Snapshot(1400).Brackets); // lost 300 ms after the last sighting, whatever the screen did
    }

    [OpenCvFact]
    public void Tracker_APlateWithoutATemplateCountsAsSeenWhenItsDetectionArrives()
    {
        using var tracker = new PlateTemplateTracker();
        var box = new PixelRect(300, 200, 120, 24);
        var seed = new PlateSeed(1, box, box, null, PlateColorClass.Any, 0, 0, 1000); // picture from 1000 ms
        var bracket = new LiveBracket(1, "P", box, 0, 0, 1000, MatchKind.NoMatch, OverlayColor.Blue);

        tracker.ApplyDetection(new DetectionResult(new LiveSnapshot(new[] { bracket }, Array.Empty<PanelEntry>(), 1000), new[] { seed }, 1000, W, H), 1250); // OCR took 250 ms

        Assert.Single(tracker.Snapshot(1500).Brackets); // 250 ms after it arrived: still there, no blink before the next detection
        Assert.Empty(tracker.Snapshot(1600).Brackets);  // but not forever
    }

    // ----- the search patch -----

    [OpenCvTheory]
    [InlineData(5.0, 1440, 16.0)]     // a fast tick: the minimum margin
    [InlineData(33.3, 1440, 66.6)]    // 2000 px/s x tick
    [InlineData(33.3, 720, 33.3)]     // scaled with the window height
    [InlineData(8.3, 2160, 24.9)]
    public void Tracker_TheSearchMarginIsTheLargerOf16AndTheSpeedLimitTimesTheTick_ScaledToTheHeight(double tickMs, int height, double expected)
    {
        using var tracker = new PlateTemplateTracker();

        Assert.Equal(expected, tracker.Margin(tickMs, height), 1);
    }

    [OpenCvFact]
    public void Tracker_ThePatchIsTheTemplateBoxGrownByTheMarginAndClippedToTheWindow()
    {
        using var tracker = new PlateTemplateTracker();
        using var first = Window();
        DrawPlate(first, 400, 500, White);
        var name = NameBoxAt(400, 500);
        tracker.ApplyDetection(Detection(first, 400, 500, 1000), 1000);

        var plan = Assert.Single(tracker.Plan(1000, 33.3, W, H));
        var margin = tracker.Margin(33.3, H);

        Assert.InRange(plan.Patch.X, 400 - margin - 1, 400 - margin + 1);
        Assert.InRange(plan.Patch.Width, name.Width + 2 * margin - 2, name.Width + 2 * margin + 3);
        Assert.InRange(plan.Patch.Height, name.Height + 2 * margin - 2, name.Height + 2 * margin + 3);

        // at the window's edge the patch stops at the edge
        using var edge = Window();
        DrawPlate(edge, 4, 6, White);
        tracker.ApplyDetection(Detection(edge, 4, 6, 1100), 1100);
        var clipped = Assert.Single(tracker.Plan(1100, 33.3, W, H));
        Assert.Equal(0, clipped.Patch.X);
        Assert.Equal(0, clipped.Patch.Y);
    }

    [OpenCvFact]
    public void Tracker_TheTemplateIsOnlyTheNameRowsNotTheDistanceLine()
    {
        using var window = Window();
        DrawPlate(window, 200, 300, White);
        var name = NameBoxAt(200, 300);
        // a distance line under the name: it must not be in the template, and must not change the match
        Cv2.PutText(window, "12.4km", new Point(200, 300 + name.Height + 20), HersheyFonts.HersheySimplex, 0.9, White, 2);
        using var tracker = new PlateTemplateTracker();
        tracker.ApplyDetection(Detection(window, 200, 300, 1000), 1000);

        var plan = Assert.Single(tracker.Plan(1000, 33, W, H));
        var grown = name.Width + 2 * tracker.Margin(33, H);
        Assert.InRange(plan.Patch.Width, grown - 3, grown + 3); // the name's width: the distance line is not part of the template

        using var changed = Window();
        DrawPlate(changed, 200, 300, White);
        Cv2.PutText(changed, "987.3km", new Point(200, 300 + name.Height + 20), HersheyFonts.HersheySimplex, 0.9, White, 2); // the distance changed
        var tick = Tick(tracker, changed, 1033);

        Assert.Equal(1, tick.Found);
        Assert.InRange(Assert.Single(tracker.Snapshot(1033).Brackets).Box.X, 199.0, 201.0);
    }

    // ----- seeds and masks -----

    [OpenCvFact]
    public void Seeds_ATemplateIsTheMaskedTextOfTheNameBox()
    {
        using var window = Window();
        DrawPlate(window, 200, 300, White);
        using var builder = new PlateSeedBuilder();
        var box = NameBoxAt(200, 300);

        using var template = builder.MakeTemplate(window, ToRect(box), H, out var cls);

        Assert.NotNull(template);
        Assert.Equal(PlateColorClass.CyanWhite, cls);
        Assert.Equal((int)box.Width, template!.Width);
        Assert.Equal((int)box.Height, template.Height);
        Assert.Equal(MatType.CV_8UC1, template.Type());
        Assert.True(Cv2.CountNonZero(template) > 50);
    }

    [OpenCvFact]
    public void Seeds_AnEmptyBoxHasNoTemplate()
    {
        using var window = Window();
        using var builder = new PlateSeedBuilder();

        Assert.Null(builder.MakeTemplate(window, new Rect(200, 300, 120, 30), H, out _));
        Assert.Null(builder.MakeTemplate(window, new Rect(5000, 5000, 120, 30), H, out _));
    }

    [OpenCvFact]
    public void Masker_ClassifiesTheTextByItsColour()
    {
        using var masker = new PlateMasker();
        var box = ToRect(NameBoxAt(200, 300));

        foreach (var (color, expected) in new[]
        {
            (new Scalar(255, 255, 255), PlateColorClass.CyanWhite),
            (new Scalar(255, 255, 0), PlateColorClass.CyanWhite),   // cyan (BGR)
            (new Scalar(50, 50, 230), PlateColorClass.Red),
            (new Scalar(60, 230, 60), PlateColorClass.Green),
        })
        {
            using var window = Window();
            DrawPlate(window, 200, 300, color);
            Assert.Equal(expected, masker.Classify(window, H, box));
        }

        using var blank = Window();
        Assert.Equal(PlateColorClass.Any, masker.Classify(blank, H, box));
    }

    [OpenCvFact]
    public void Masker_TheCombinedClassHoldsTheThreeClasses()
    {
        using var masker = new PlateMasker();
        using var window = Window();
        DrawPlate(window, 100, 100, White);
        DrawPlate(window, 100, 200, new Scalar(50, 50, 230));
        DrawPlate(window, 100, 300, new Scalar(60, 230, 60));
        using var any = new Mat();
        using var cyan = new Mat();
        using var red = new Mat();
        using var green = new Mat();

        masker.Build(window, H, PlateColorClass.Any, any);
        masker.Build(window, H, PlateColorClass.CyanWhite, cyan);
        masker.Build(window, H, PlateColorClass.Red, red);
        masker.Build(window, H, PlateColorClass.Green, green);

        Assert.True(Cv2.CountNonZero(cyan) > 0 && Cv2.CountNonZero(red) > 0 && Cv2.CountNonZero(green) > 0);
        Assert.Equal(Cv2.CountNonZero(cyan) + Cv2.CountNonZero(red) + Cv2.CountNonZero(green), Cv2.CountNonZero(any));
        Assert.Equal(1, masker.KernelSize(1) % 2);
        Assert.Equal(11, masker.KernelSize(1440));
    }

    [OpenCvFact]
    public void Tracker_ARedPlateIsFollowedWithTheRedMask()
    {
        using var tracker = new PlateTemplateTracker();
        using (var first = Window())
        {
            DrawPlate(first, 200, 300, new Scalar(50, 50, 230));
            tracker.ApplyDetection(Detection(first, 200, 300, 1000), 1000);
        }

        using var moved = Window();
        DrawPlate(moved, 212, 296, new Scalar(50, 50, 230));
        var tick = Tick(tracker, moved, 1033);

        Assert.Equal(1, tick.Found);
        var bracket = Assert.Single(tracker.Snapshot(1033).Brackets);
        Assert.InRange(bracket.Box.X, 211.0, 213.0);
        Assert.InRange(bracket.Box.Y, 295.0, 297.0);
    }

    // ----- the hand-over between the capture loop and the detection thread -----

    [OpenCvFact]
    public void Mailbox_OnePictureAtATime_AndTheCaptureLoopNeverWaits()
    {
        using var mailbox = new DetectionMailbox();
        using var picture = new Mat(40, 60, MatType.CV_8UC3, new Scalar(1, 2, 3));
        var window = new PixelRect(0, 0, 60, 40);

        Assert.True(mailbox.IsFree);
        Assert.True(mailbox.TryPost(picture, window, 100, epoch: 1));
        Assert.False(mailbox.TryPost(picture, window, 200, epoch: 1)); // not taken yet: skipped, not queued

        Assert.True(mailbox.TryTake(out var job));
        Assert.NotNull(job);
        Assert.Equal(100, job!.CaptureMs);
        Assert.Equal(1, job.Epoch);
        Assert.Equal(60, job.Image.Width);
        Assert.False(mailbox.TryPost(picture, window, 300, epoch: 1)); // being read: skipped
        Assert.False(mailbox.TryTake(out _));

        mailbox.Complete();
        Assert.True(mailbox.IsFree);
        Assert.True(mailbox.TryPost(picture, window, 400, epoch: 2));
    }

    [OpenCvFact]
    public void Mailbox_ThePostedPictureIsACopy()
    {
        using var mailbox = new DetectionMailbox();
        using var picture = new Mat(10, 10, MatType.CV_8UC3, new Scalar(9, 9, 9));
        Assert.True(mailbox.TryPost(picture, new PixelRect(0, 0, 10, 10), 1, 0));

        picture.SetTo(new Scalar(0, 0, 0)); // the capture loop reuses its picture at once

        Assert.True(mailbox.TryTake(out var job));
        Assert.Equal(9, job!.Image.At<Vec3b>(5, 5).Item0);
    }

    [OpenCvFact]
    public void Mailbox_ClearDropsTheWaitingPicture()
    {
        using var mailbox = new DetectionMailbox();
        using var picture = new Mat(10, 10, MatType.CV_8UC3);
        mailbox.TryPost(picture, new PixelRect(0, 0, 10, 10), 1, 0);

        mailbox.Clear();

        Assert.False(mailbox.TryTake(out _));
        Assert.True(mailbox.IsFree);
    }

    [OpenCvFact]
    public void ResultSlot_ANewerResultReplacesAnOlderOneThatWasNotTaken()
    {
        var slot = new DetectionResultSlot();
        var template = new Mat(4, 4, MatType.CV_8UC1);
        var older = new DetectionResult(LiveSnapshot.Empty, new[] { new PlateSeed(1, default, default, template, PlateColorClass.Any, 0, 0, 1) }, 1, W, H);
        var newer = DetectionResult.Empty(2, W, H);

        slot.Post(older);
        slot.Post(newer);

        Assert.True(template.IsDisposed); // the replaced result's seeds were disposed
        Assert.True(slot.TryTake(out var taken));
        Assert.Same(newer, taken);
        Assert.False(slot.TryTake(out _));
    }

    [Fact]
    public void ResultSlot_AResultCanBeStampedWithAnotherEpoch()
    {
        var result = DetectionResult.Empty(5, W, H, epoch: 1).WithEpoch(7);

        Assert.Equal(7, result.Epoch);
        Assert.Equal(5, result.CaptureMs);
    }

    // ----- the history of published brackets -----

    [Fact]
    public void History_KeepsTheLastThreeSetsNewestFirst()
    {
        var history = new PublishedHistory<int>(3);
        Assert.Empty(history.Recent);

        for (var i = 1; i <= 5; i++)
            history.Publish(new[] { i });

        Assert.Equal(new[] { 5, 4, 3 }, history.Recent.Select(set => set[0]));
    }

    [Fact]
    public void History_AnEmptySetAfterAnEmptySetAddsNothing_AndClearForgetsAll()
    {
        var history = new PublishedHistory<int>(3);
        history.Publish(new[] { 1 });
        history.Publish(Array.Empty<int>());
        history.Publish(Array.Empty<int>());
        history.Publish(Array.Empty<int>());

        Assert.Equal(2, history.Recent.Count); // the idle overlay did not wash the last real set out
        Assert.Equal(1, history.Recent[1][0]);

        history.Clear();

        Assert.Empty(history.Recent);
    }

    [Fact]
    public async Task History_APublisherAndAReaderOnTwoThreadsAlwaysSeeConsistentLists()
    {
        var history = new PublishedHistory<int>(3);
        var stop = false;
        var failures = 0;
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var recent = history.Recent;
                if (recent.Count > 3)
                    Interlocked.Increment(ref failures);
                for (var i = 1; i < recent.Count; i++)
                {
                    if (recent[i][0] >= recent[i - 1][0]) // newest first: values published in rising order
                        Interlocked.Increment(ref failures);
                }
            }
        });

        for (var i = 1; i <= 20000; i++)
            history.Publish(new[] { i });
        Volatile.Write(ref stop, true);
        await reader;

        Assert.Equal(0, failures);
    }

    // ----- statistics -----

    [Fact]
    public void LiveStats_TheSummaryHasTheTrackingNumbers()
    {
        var stats = new LiveStats(0);
        stats.AddRead(40);
        for (var i = 1; i <= 100; i++)
            stats.AddTrackTick(i / 10.0, 2, i % 4 == 0 ? 1 : 2); // 100 ticks of 0.1 to 10 ms; 200 matches looked for, 175 found

        var line = stats.TakeSummary(60_000, 3);

        Assert.Contains("1 reads in 60 s", line);
        Assert.Contains("tracking: 100 ticks", line);
        Assert.Contains("average 5.050 ms", line);
        Assert.Contains("p95 9.500 ms", line);
        Assert.Contains("87.5 % of 200 matches found", line);
        Assert.Equal(0, stats.TrackTicks); // a new window
    }

    [Fact]
    public void RenderStats_CountsFramesRedrawsAndTheAverageCost()
    {
        var stats = new RenderStats(0);
        stats.AddFrame(0.2, redrew: false);
        stats.AddFrame(0.6, redrew: true);
        stats.AddFrame(0.4, redrew: true);

        Assert.False(stats.IsDue(59_000, 60_000));
        Assert.True(stats.IsDue(60_000, 60_000));
        var line = stats.TakeSummary(60_000);

        Assert.Equal("Overlay render: 3 frames in 60 s, 2 redraws, average 0.400 ms per frame", line);
        Assert.Equal(0, stats.Frames);
    }
}
