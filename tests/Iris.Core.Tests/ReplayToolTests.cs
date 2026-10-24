using Iris.Core.Detection;
using System.Text.RegularExpressions;
using Iris.Replay;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The developer tool tools/Iris.Replay: command line, label files, recall measures and one run over the synthetic frames.
/// Two tests redirect Console.Out / Console.Error, which are process wide: the "Console" collection keeps them from running in
/// parallel with other test classes.
/// </summary>
[Collection("Console")]
public class ReplayToolTests
{
    // ------------------------------------------------------------------------------------------------ command line

    [Fact]
    public void Options_ReadEverything()
    {
        var options = ReplayOptions.Parse(
            new[] { "clips", "--labels", "clips.txt", "--out", "somewhere", "--annotate", "--every", "5", "--region", "0.1, 0.9, 0.2, 0.8", "--fps", "5", "--image-fps", "25" }, out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("clips", options!.Input);
        Assert.Equal("clips.txt", options.LabelsPath);
        Assert.Equal("somewhere", options.OutDirectory);
        Assert.True(options.Annotate);
        Assert.Equal(5, options.Every);
        Assert.Equal(5.0, options.SampleFps);
        Assert.Equal(25.0, options.ImageFps);
        Assert.Equal(0.1, options.Settings.RegionLeft);
        Assert.Equal(0.9, options.Settings.RegionRight);
        Assert.Equal(0.2, options.Settings.RegionTop);
        Assert.Equal(0.8, options.Settings.RegionBottom);
    }

    [Fact]
    public void Options_HaveSensibleDefaults()
    {
        var options = ReplayOptions.Parse(new[] { "video.mp4" }, out var error);

        Assert.Null(error);
        Assert.Equal(1, options!.Every);
        Assert.Null(options.SampleFps);   // every frame
        Assert.Equal(10.0, options.ImageFps);
        Assert.False(options.Annotate);
        Assert.Null(options.LabelsPath);
        Assert.Equal(NameplateDetectorSettings.Default, options.Settings);
        Assert.EndsWith(Path.Combine("Iris", "replay"), options.OutDirectory);
    }

    /// <summary>Argument lists the parser must refuse. TheoryData, because an InlineData array argument would be spread into the parameters.</summary>
    public static TheoryData<string[]> BadArgs => new()
    {
        new string[0],
        new[] { "a", "b" },
        new[] { "a", "--every" },
        new[] { "a", "--every", "0" },
        new[] { "a", "--every", "x" },
        new[] { "a", "--fps" },
        new[] { "a", "--fps", "0" },
        new[] { "a", "--fps", "x" },
        new[] { "a", "--image-fps", "0" },
        new[] { "a", "--region", "0.1,0.2" },
        new[] { "a", "--region", "0.9,0.1,0.1,0.7" },
        new[] { "a", "--bogus" },
    };

    [Theory]
    [MemberData(nameof(BadArgs))]
    public void Options_RejectWhatIsWrong(string[] args)
    {
        var options = ReplayOptions.Parse(args, out var error);

        Assert.Null(options);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Options_HelpNeedsNoInput()
    {
        var options = ReplayOptions.Parse(new[] { "--help" }, out var error);

        Assert.Null(error);
        Assert.True(options!.ShowHelp);
    }

    // ------------------------------------------------------------------------------------------------ labels

    [Fact]
    public void ClipList_IsParsed()
    {
        var labels = ClipLabels.ParseClipList("# comment\n\nclipA: alpha_one, BRAVO-TWO\nclipB: CHARLIE3;DELTA4 # not a comment, it is part of the last name\nclipA: ECHO5\r\n");

        Assert.Equal(LabelMode.ByClip, labels.Mode);
        Assert.Equal(new[] { "ALPHA_ONE", "BRAVO-TWO", "ECHO5" }, labels.NamesFor("clipA"));
        Assert.Equal(new[] { "CHARLIE3", "DELTA4 # NOT A COMMENT", "IT IS PART OF THE LAST NAME" }, labels.NamesFor("clipB"));
        Assert.Null(labels.NamesFor("clipC"));
    }

    [Fact]
    public void ClipList_NeedsAColon()
    {
        Assert.Throws<FormatException>(() => ClipLabels.ParseClipList("clipA ALPHA\n"));
    }

    [Fact]
    public void ScreenshotCsv_IsParsed()
    {
        var csv = "file,expected_players,notes\r\n" +
                  "shot1.jpg,ALPHA_ONE;BRAVO-TWO,\"second one, on a \"\"bright\"\" moon\"\r\n" +
                  "shot2.jpg,,station markers only\r\n" +
                  "\r\n";

        var labels = ClipLabels.ParseScreenshotCsv(csv);

        Assert.Equal(LabelMode.ByFile, labels.Mode);
        Assert.Equal(new[] { "ALPHA_ONE", "BRAVO-TWO" }, labels.NamesFor("shot1.jpg"));
        Assert.Empty(labels.NamesFor("shot2.jpg")!);
        Assert.Null(labels.NamesFor("shot3.jpg"));
    }

    // ------------------------------------------------------------------------------------------------ metrics

    private static NameplateDetection Detection(string name) =>
        new(name, -0.1, new OpenCvSharp.Rect(1, 2, 3, 4), new OpenCvSharp.Rect(1, 8, 3, 4), "9.0km");

    [Fact]
    public void Metrics_SeparateLabeledExcludedAndWrongReads()
    {
        var labels = ClipLabels.ParseClipList("clip1: ALPHA_ONE, BRAVO-TWO\n");
        var metrics = new ReplayMetrics(labels, new[] { "Stanton", "UNKNOWN" });

        metrics.AddReads("clip1", "f1", 1, new[] { Detection("ALPHA_ONE"), Detection("UNKNOWN") }, out var m1);
        metrics.AddReads("clip1", "f2", 2, new[] { Detection("ALPHA_0NE"), Detection("ZZZZZZZZ") }, out var m2);   // a 0 for the O is another name: both wrong
        metrics.AddReads("clip1", "f3", 3, Array.Empty<NameplateDetection>(), out _);
        metrics.AddReads("clip1", "f4", 4, new[] { Detection("BRAVO-TWO"), Detection("PA-5968-XF") }, out _);    // a contact code is ignored by name
        metrics.AddReads("other", "g1", 1, new[] { Detection("ALPHA_ONE") }, out var m3);                        // clip without labels

        var g = metrics.Groups["clip1"];
        Assert.Equal(2, g.Labels);
        Assert.Equal(6, g.Reads);   // 2 + 2 + 0 + 2 (the clip without labels is not counted)
        Assert.Equal(2, g.ExcludedReads);
        Assert.Equal(2, g.WrongReads);
        Assert.Equal(2, g.ReadFrames.Count);
        Assert.Equal(new HashSet<int> { 1 }, g.ReadFrames["ALPHA_ONE"]);   // frame 2 read ALPHA_0NE: not ALPHA_ONE
        Assert.Equal("ALPHA_ONE", m1[0]);
        Assert.Null(m2[0]);
        Assert.Null(m2[1]);
        Assert.Empty(m3);
        Assert.DoesNotContain("other", metrics.Groups.Keys);
    }

    /// <summary>Runs reads through a real tracker (a track cannot be made by hand): one name per x position, a name for every frame it has reads for.</summary>
    private static IReadOnlyList<NameplateTrack> Tracks(params (int X, string?[] Reads)[] plates)
    {
        var tracker = new NameplateTracker(NameplateTrackerSettings.Default with { ConfirmReads = 2, ConfirmSpanMs = 0 });
        var frames = plates.Max(p => p.Reads.Length);
        for (var frame = 0; frame < frames; frame++)
        {
            var candidates = new List<TrackCandidate>();
            foreach (var (x, reads) in plates)
            {
                if (frame < reads.Length)
                {
                    var top = new OpenCvSharp.Rect(x, 500, 80, 12);
                    candidates.Add(new TrackCandidate(top, new OpenCvSharp.Rect(x + 10, 524, 60, 12)));
                }
            }

            var index = frame;
            tracker.Update(frame, frame * 100.0, candidates, (top, _) =>
            {
                var plate = plates.First(p => p.X == top.X);
                return new TrackEvaluation(true, index < plate.Reads.Length ? plate.Reads[index] : null);
            });
        }

        return tracker.Finish();
    }

    [Fact]
    public void Metrics_CountFoundNamesWrongConfirmedNamesAndTheKindsOfTracks()
    {
        var labels = ClipLabels.ParseClipList("clip1: ALPHA_ONE, BRAVO-TWO\n");
        var metrics = new ReplayMetrics(labels, new[] { "UNKNOWN" });
        var tracks = Tracks(
            (100, new string?[] { "ALPHA_ONE", "ALPHA_ONE", "ALPHA_ONE" }),   // found, exact, confirmed at frame 1
            (400, new string?[] { "ZZZZZZZZ", "ZZZZZZZZ" }),                  // confirmed, and wrong
            (700, new string?[] { "UNKNOWN", "UNKNOWN" }),                    // confirmed, but ignored by name
            (1000, new string?[] { "ALPHA_O", null }),                        // a stage of the typing animation: one read, never confirmed
            (1300, new string?[] { "QXQXQXQX", null }),                       // junk: one read, a name nobody knows
            (1600, new string?[] { null, null }));                            // a nameplate that was never read surely

        foreach (var track in tracks)
            metrics.AddTrack("clip1", track);
        for (var i = 0; i < 3; i++)
            metrics.AddFrame("clip1", "f", 10.0 + i);

        var g = metrics.Groups["clip1"];
        var found = Assert.Single(g.Found);
        Assert.Equal("ALPHA_ONE", found.Key);
        Assert.Equal(1, found.Value.DelayFrames);
        Assert.Equal(100.0, found.Value.DelayMs);
        Assert.Equal(new[] { "ZZZZZZZZ" }, g.WrongConfirmed);
        Assert.Equal(new[] { "ZZZZZZZZ" }, g.WrongFinal);
        Assert.Equal(0, g.Corrections);
        Assert.Equal(new[] { "UNKNOWN" }, g.FilteredConfirmed);
        Assert.Equal(6, g.NameplateTracks);
        Assert.Equal(1, g.AnimationTracks);
        Assert.Equal(2, g.JunkTracks);   // QXQXQXQX and the track that confirmed ZZZZZZZZ
        Assert.Equal(1, g.UnreadTracks);
        Assert.Equal(1, g.FilteredTracks);
        Assert.Equal(11.0, g.FrameMs.Count == 0 ? 0 : Program.Median(g.FrameMs));

        var text = metrics.Format();
        Assert.Contains("TOTAL", text);
        Assert.Contains("clip1: BRAVO-TWO", text);          // never confirmed
        Assert.Contains("clip1: ZZZZZZZZ", text);           // listed as WRONG
    }

    [Fact]
    public void Metrics_ALookAlikeConfirmation_IsWrong_NotFound()
    {
        var labels = ClipLabels.ParseClipList("clip1: ALPHA_ONE\n");
        var metrics = new ReplayMetrics(labels, Array.Empty<string>());
        foreach (var track in Tracks((100, new string?[] { "ALPHA_0NE", "ALPHA_0NE" })))
            metrics.AddTrack("clip1", track);

        var g = metrics.Groups["clip1"];
        Assert.Empty(g.Found);   // names match exactly: the zero is not an O
        Assert.Equal(new[] { "ALPHA_0NE" }, g.WrongConfirmed);
    }

    [Fact]
    public void Metrics_CountCorrections_AndTheWrongNamesThatRemain()
    {
        var labels = ClipLabels.ParseClipList("clip1: ALPHA_ONE\n");
        var metrics = new ReplayMetrics(labels, Array.Empty<string>());
        // confirmed ZZZZZZZZ at frame 1; a confirmed track is read once a second, then ALPHA_ONE is read three times (frames 11 to 13) and corrects it
        var reads = new string?[14];
        reads[0] = reads[1] = "ZZZZZZZZ";
        reads[11] = reads[12] = reads[13] = "ALPHA_ONE";
        var track = Assert.Single(Tracks((100, reads)));
        Assert.Equal("ALPHA_ONE", track.ConfirmedName);
        Assert.Single(track.Corrections);

        metrics.AddTrack("clip1", track);

        var g = metrics.Groups["clip1"];
        Assert.Equal(1, g.Corrections);
        Assert.Equal(new[] { "ZZZZZZZZ" }, g.WrongConfirmed);   // it was shown for a while
        Assert.Empty(g.WrongFinal);                              // but it is not what the track ended with
        var found = g.Found["ALPHA_ONE"];
        Assert.Equal(2, found.DelayFrames);                      // first read at frame 11, confirmed at frame 13
        Assert.Equal(200.0, found.DelayMs);
        Assert.Contains("corr", metrics.Format());
    }

    [Fact]
    public void Metrics_ForScreenshotsCountEachImage()
    {
        var labels = ClipLabels.ParseScreenshotCsv("file,expected_players\nshot1.jpg,ALPHA_ONE\nshot2.jpg,\n");
        var metrics = new ReplayMetrics(labels, Array.Empty<string>());

        metrics.AddFrame("Names", "shot1.jpg", 5.0);
        metrics.AddReads("Names", "shot1.jpg", 1, new[] { Detection("ALPHA_ONE") }, out _);
        metrics.AddFrame("Names", "shot2.jpg", 5.0);
        metrics.AddReads("Names", "shot2.jpg", 2, new[] { Detection("ALPHA_ONE") }, out _);   // not expected there: wrong

        Assert.Single(metrics.Groups["shot1.jpg"].ReadFrames);
        Assert.Equal(1, metrics.Groups["shot2.jpg"].WrongReads);
        Assert.Equal(0, metrics.Groups["shot2.jpg"].Labels);
        Assert.Contains("TOTAL", metrics.Format());
    }

    [Theory]
    [InlineData("ALPHA_ONE", "ALPHA_ONE", 0)]
    [InlineData("ALPHA_ONE", "ALPHA_ONF", 1)]
    [InlineData("", "ABC", 3)]
    public void EditDistance_IsTheLevenshteinDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, ReplayMetrics.EditDistance(a, b));
    }

    // ------------------------------------------------------------------------------------------------ sampling by time

    /// <summary>Which of the frames of a video of the given rate a sampler keeps (frame numbers from 0), as the replay loop uses it.</summary>
    private static List<int> Kept(double videoFps, int frames, double? sampleFps)
    {
        var sampler = new FrameSampler(sampleFps);
        var kept = new List<int>();
        for (var n = 0; n < frames; n++)
        {
            var time = n * 1000.0 / videoFps;
            if (!sampler.Due(time))
                continue;
            sampler.Kept(time);
            kept.Add(n);
        }

        return kept;
    }

    [Fact]
    public void Sampler_WithoutARate_KeepsEveryFrame()
    {
        Assert.Equal(Enumerable.Range(0, 50), Kept(60.0, 50, null));
    }

    [Theory]
    [InlineData(60.0, 12)]    // 200 ms is 12 frames of 60 fps
    [InlineData(120.0, 24)]
    [InlineData(30.0, 6)]
    [InlineData(144.0, 29)]   // 28.8 frames: the 29th frame is the first one 200 ms later
    public void Sampler_KeepsOneFrameEvery200Ms_At5Fps(double videoFps, int every)
    {
        var kept = Kept(videoFps, 600, 5.0);

        Assert.Equal(Enumerable.Range(0, 600).Where(n => n % every == 0), kept);
        for (var i = 1; i < kept.Count; i++)
            Assert.True((kept[i] - kept[i - 1]) * 1000.0 / videoFps >= 200.0 - 0.001);
    }

    [Fact]
    public void Sampler_ByTime_NotByFrameCount()
    {
        // 7 fps is 142.86 ms: 60 fps frames are 16.67 ms apart, so the 9th frame (150 ms) is the first that is due
        Assert.Equal(new[] { 0, 9, 18, 27 }, Kept(60.0, 28, 7.0));
    }

    [Fact]
    public void Sampler_AFasterRateThanTheVideo_KeepsEveryFrame()
    {
        Assert.Equal(Enumerable.Range(0, 20), Kept(10.0, 20, 30.0));
        Assert.Equal(Enumerable.Range(0, 20), Kept(10.0, 20, 10.0));
    }

    [Fact]
    public void Sampler_DueDoesNotChangeIt_OnlyKeptDoes()
    {
        var sampler = new FrameSampler(5.0);
        Assert.True(sampler.Due(0));
        Assert.True(sampler.Due(0));
        sampler.Kept(0);
        Assert.False(sampler.Due(199.0));
        Assert.False(sampler.Due(199.0));
        Assert.True(sampler.Due(200.0));
        sampler.Kept(210.0);   // the next one is due 200 ms after the frame that was kept, not after the grid
        Assert.False(sampler.Due(400.0));
        Assert.True(sampler.Due(410.0));
    }

    [Fact]
    public void FpsIsIgnoredForSingleImages_AndTheToolSaysSo()
    {
        var oneImage = new[] { new ClipInput("shot", null, new[] { "shot.png" }) };
        var video = new[] { new ClipInput("clip", "clip.mp4", Array.Empty<string>()) };
        var folder = new[] { new ClipInput("frames", null, new[] { "f1.png", "f2.png" }) };

        Assert.Null(Program.FpsIgnoredWarning(null, true, oneImage));             // no --fps: nothing to say
        Assert.Null(Program.FpsIgnoredWarning(5.0, false, video));                // a video is sampled
        Assert.Null(Program.FpsIgnoredWarning(5.0, false, folder));               // so is a folder of frames
        Assert.Contains("--fps is ignored", Program.FpsIgnoredWarning(5.0, false, oneImage));  // one image as the input
        Assert.Contains("--fps is ignored", Program.FpsIgnoredWarning(5.0, true, folder));     // screenshot labels: every image alone
    }

    [Fact]
    public void Sampler_RefusesARateOfZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameSampler(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameSampler(-5.0));
    }

    [OpenCvFact]
    public void ImageFrames_AreSampledByTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris-replay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            for (var i = 1; i <= 10; i++)
                File.Copy(Path.Combine(DetectorFixtures.Dir, "frame_d.png"), Path.Combine(root, $"f{i}.png"));
            var clip = Assert.Single(FrameSources.Discover(root));

            var numbers = new List<int>();
            var times = new List<double>();
            foreach (var frame in FrameSources.Frames(clip, 1, null, 10.0, new FrameSampler(5.0)))
            {
                using var image = frame.Image;
                numbers.Add(frame.Number);
                times.Add(frame.TimeMs);
            }

            Assert.Equal(new[] { 1, 3, 5, 7, 9 }, numbers);   // 10 fps images, 5 fps wanted: every second one
            Assert.Equal(new[] { 0.0, 200.0, 400.0, 600.0, 800.0 }, times);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ------------------------------------------------------------------------------------------------ files

    [Theory]
    [InlineData("f00041.jpg", 41)]
    [InlineData("clip1_000123.png", 123)]
    [InlineData("Sample-Capture_Invented-C92.jpg", 92)]
    public void TrailingNumber_IsTheLastDigitRun(string file, int expected)
    {
        Assert.Equal(expected, FrameSources.TrailingNumber(file));
    }

    [Fact]
    public void TrailingNumber_IsNullWithoutDigits()
    {
        Assert.Null(FrameSources.TrailingNumber("frame.png"));
    }

    [Fact]
    public void ImageFrames_AreInFrameOrderNotNameOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris-replay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var name in new[] { "f10.png", "f2.png", "cover.png", "f1.png", "a_100.png" })
                File.WriteAllBytes(Path.Combine(root, name), new byte[] { 0 });

            var clip = Assert.Single(FrameSources.Discover(root));
            Assert.Equal(new[] { "f1.png", "f2.png", "f10.png", "a_100.png", "cover.png" }, clip.Images.Select(p => Path.GetFileName(p)!));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GitRepositories_AreRecognized()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris-replay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "plain", "deeper"));
            Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));

            Assert.True(Program.IsInsideGitRepository(Path.Combine(root, "repo", "out", "not-created-yet")));
            Assert.True(Program.IsInsideGitRepository(Path.Combine(root, "repo")));
            Assert.False(Program.IsInsideGitRepository(Path.Combine(root, "plain", "deeper")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ------------------------------------------------------------------------------------------------ a run

    /// <summary>
    /// The tool over the synthetic detector frames, as a folder of images: it writes detections.csv with the nameplates of the
    /// frames, the label measures, and annotated images.
    /// Note: the input and output folders are made in the system temp folder (%TEMP%). The tool refuses to write inside a git
    /// repository, so if %TEMP% itself lies inside one (a .git folder in a parent folder), this test fails with exit code 2.
    /// </summary>
    [OpenCvFact]
    public void ReplayOverSyntheticFrames_WritesTheReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris-replay-test-" + Guid.NewGuid().ToString("N"));
        var frames = Path.Combine(root, "synthetic");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(frames);
        try
        {
            // the same frame three times (so that a track can read its name repeatedly), then a frame of stars only; numbered, 10 fps
            for (var i = 1; i <= 3; i++)
                File.Copy(Path.Combine(DetectorFixtures.Dir, "frame_a.png"), Path.Combine(frames, $"frame_a_{i}.png"));
            File.Copy(Path.Combine(DetectorFixtures.Dir, "frame_d.png"), Path.Combine(frames, "frame_d_4.png"));
            var labels = Path.Combine(root, "clips.txt");
            File.WriteAllText(labels, "synthetic: SYNTH_ALPHA, SYNTH_BRAVO, DUMMY_DELTA, FIXTURE_ECHO\n");

            var console = new StringWriter();
            var original = Console.Out;
            int exit;
            Console.SetOut(console);
            try
            {
                exit = Program.Main(new[] { frames, "--labels", labels, "--out", output, "--annotate", "--confirm-reads", "2", "--confirm-span", "0" });
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal(0, exit);
            var lines = File.ReadAllLines(Path.Combine(output, "detections.csv"));
            Assert.StartsWith("source,frame,track,verdict,confirmed,name,score,", lines[0]);
            // frame 3 is not evaluated: the track confirmed its name at frame 2 and is not re-read within ConfirmedRereadMs
            Assert.Contains(lines, l => l.StartsWith("synthetic/frame_a_2.png,") && l.Contains(",SYNTH_ALPHA,"));
            Assert.DoesNotContain(lines, l => l.StartsWith("synthetic/frame_d_4.png,")); // stars only
            var tracks = File.ReadAllLines(Path.Combine(output, "tracks.csv"));
            Assert.StartsWith("clip,track,", tracks[0]);
            Assert.Contains(tracks, l => l.Contains(",SYNTH_ALPHA,SYNTH_ALPHA,"));   // best name, confirmed name
            Assert.Contains(tracks, l => l.Contains(",DUMMY_DELTA,DUMMY_DELTA,"));
            var text = console.ToString();
            Assert.Matches(new Regex(@"^TOTAL\s+4\s", RegexOptions.Multiline), text);   // 4 frames replayed
            Assert.Matches(new Regex(@"^TOTAL\s+4\s+4\s+\|\s+4/4\s", RegexOptions.Multiline), text);   // 4 labels, all 4 confirmed
            Assert.True(File.Exists(Path.Combine(output, "annotated", "synthetic", "frame_a_2.png")));
            Assert.False(File.Exists(Path.Combine(output, "annotated", "synthetic", "frame_d_4.png")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReplayRefusesToWriteInsideARepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris-replay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            var exit = Program.Main(new[] { root, "--out", Path.Combine(root, "out") });

            Assert.Equal(2, exit);
            Assert.Contains("git repository", error.ToString());
            Assert.False(Directory.Exists(Path.Combine(root, "out")));
        }
        finally
        {
            Console.SetError(original);
            Directory.Delete(root, true);
        }
    }
}

/// <summary>Tests that redirect the process wide console streams do not run in parallel with any other test.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection
{
}
