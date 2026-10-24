using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Iris.Core.Live;
using Iris.Replay;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The tracking simulation of tools/Iris.Replay (--track): the option, the statistics, and one run over a synthetic frame shown again and again.</summary>
public class TrackSimulationTests
{
    [Fact]
    public void Option_TrackIsOffByDefaultAndSwitchedOnByTheFlag()
    {
        Assert.False(ReplayOptions.Parse(new[] { "clip.mp4" }, out _)!.Track);

        var options = ReplayOptions.Parse(new[] { "clip.mp4", "--track", "--fps", "5" }, out var error);

        Assert.Null(error);
        Assert.True(options!.Track);
        Assert.Equal(5.0, options.SampleFps);
        Assert.Contains("--track", ReplayOptions.Usage);
    }

    [Fact]
    public void Stats_ErrorMedianAndP95AreOfTheKeptPlates()
    {
        var stats = new TrackingStats("clip");
        for (var i = 1; i <= 20; i++)
            stats.AddContinuing(true, i, 0);   // errors 1 to 20 px
        stats.AddContinuing(false);            // a plate whose bracket had gone: not in the error, but in the share

        Assert.Equal(21, stats.Continuing);
        Assert.Equal(20, stats.Kept);
        Assert.Equal(11.0, stats.ErrorMedian);
        Assert.Equal(19.0, stats.ErrorP95);
        Assert.Equal(20.0 / 21.0, stats.KeptShare, 6);
    }

    [Fact]
    public void Stats_TheShownShareIsOfTheFramesBetweenTheFirstAndTheLastDetectionThatSawAPlate()
    {
        var stats = new TrackingStats("clip");
        // frames at 0, 100, ..., 1000 ms; plates seen at 300 and 700 ms; nothing is shown before 300 ms and after 700 ms, and one frame (500 ms) shows none
        for (var t = 0; t <= 1000; t += 100)
            stats.AddFrame(t, t >= 300 && t <= 700 && t != 500 ? 1 : 0);
        stats.AddDetection(0, 0);
        stats.AddDetection(300, 1);
        stats.AddDetection(700, 1);

        Assert.Equal(5, stats.SpanFrames);       // 300, 400, 500, 600, 700
        Assert.Equal(0.8, stats.ShownShare, 6);  // all but the one at 500 ms
        Assert.Equal(3, stats.Detections);
    }

    [Fact]
    public void Stats_NothingSeenMeansZeroShares()
    {
        var stats = new TrackingStats("empty");
        stats.AddFrame(0, 0);

        Assert.Equal(0.0, stats.ShownShare);
        Assert.Equal(0.0, stats.FoundShare);
        Assert.Equal(0.0, stats.KeptShare);
        Assert.Equal(0, stats.SpanFrames);
        Assert.Equal(0.0, stats.ErrorMedian);
    }

    [Fact]
    public void Stats_TheFoundShareAndTheTickCostAreOfTheMatches()
    {
        var stats = new TrackingStats("clip");
        stats.AddTick(0.2, 2, 2);
        stats.AddTick(0.4, 2, 1);
        stats.AddTick(0.6, 0, 0);

        Assert.Equal(0.75, stats.FoundShare, 6);
        Assert.Equal(0.4, stats.TickMedianMs, 6);
        Assert.StartsWith("clip", stats.Row("clip"));
        Assert.Contains("75.0", stats.Row("clip"));
    }

    [OpenCvFact]
    public void RunClip_APlateThatStandsStillIsTrackedWithoutErrorAndShownOnEveryFrame()
    {
        var frame = DetectorFixtures.Load().Single(f => f.File == "frame_c.png");
        Assert.Single(frame.Default.Detections);
        using var reference = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var detector = new NameplateDetector(new NameplateReader(LetterModel.LoadEmbedded()));

        // 2 seconds of a 30 fps clip of the same picture: detection at 5 a second confirms the name after 3 reads, tracking carries it in between
        IEnumerable<SourceFrame> Frames()
        {
            for (var i = 0; i < 60; i++)
                yield return new SourceFrame("still", i + 1, "still#" + (i + 1), reference.Clone(), i * 1000.0 / 30.0);
        }

        var rows = new List<string>();
        var stats = TrackSimulation.RunClip(
            "still", Frames(), detector, NameplateDetectorSettings.Default, NameplateTrackerSettings.Default, 5.0, LiveSettings.Default, rows);

        Assert.InRange(stats.Detections, 9, 11);
        Assert.True(stats.Continuing >= 3, "the plate is carried from detection to detection");
        Assert.Equal(1.0, stats.KeptShare);
        Assert.InRange(stats.ErrorP95, 0.0, 1.5);
        Assert.InRange(stats.ShownShare, 0.99, 1.0);
        Assert.InRange(stats.FoundShare, 0.95, 1.0);
        Assert.Equal(stats.Errors.Count, rows.Count);
        Assert.StartsWith("still,", rows[0]);
    }
}
