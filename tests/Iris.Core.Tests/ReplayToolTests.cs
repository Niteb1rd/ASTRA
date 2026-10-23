using Iris.Core.Detection;
using Iris.Replay;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The developer tool tools/Iris.Replay: command line, label files, recall measures and one run over the synthetic frames.</summary>
public class ReplayToolTests
{
    // ------------------------------------------------------------------------------------------------ command line

    [Fact]
    public void Options_ReadEverything()
    {
        var options = ReplayOptions.Parse(
            new[] { "clips", "--labels", "clips.txt", "--out", "somewhere", "--annotate", "--every", "5", "--region", "0.1, 0.9, 0.2, 0.8" }, out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("clips", options!.Input);
        Assert.Equal("clips.txt", options.LabelsPath);
        Assert.Equal("somewhere", options.OutDirectory);
        Assert.True(options.Annotate);
        Assert.Equal(5, options.Every);
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
        Assert.False(options.Annotate);
        Assert.Null(options.LabelsPath);
        Assert.Equal(NameplateDetectorSettings.Default, options.Settings);
        Assert.EndsWith(Path.Combine("Iris", "replay"), options.OutDirectory);
    }

    [Theory]
    [InlineData(new string[0])]
    [InlineData(new[] { "a", "b" })]
    [InlineData(new[] { "a", "--every" })]
    [InlineData(new[] { "a", "--every", "0" })]
    [InlineData(new[] { "a", "--every", "x" })]
    [InlineData(new[] { "a", "--region", "0.1,0.2" })]
    [InlineData(new[] { "a", "--region", "0.9,0.1,0.1,0.7" })]
    [InlineData(new[] { "a", "--bogus" })]
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

    private static NameplateDetection Detection(string name, bool stacked = false) =>
        new(name, -0.1, new OpenCvSharp.Rect(1, 2, 3, 4), new OpenCvSharp.Rect(1, 8, 3, 4), "9-0K-", stacked);

    [Fact]
    public void Metrics_SeparateLabeledExcludedAndWrongReads()
    {
        var labels = ClipLabels.ParseClipList("clip1: ALPHA_ONE, BRAVO-TWO\n");
        var metrics = new ReplayMetrics(labels, new[] { "Stanton", "UNKNOWN" });

        metrics.AddFrame("clip1", "f1", 1, new[] { Detection("ALPHA_ONE"), Detection("UNKNOWN") }, out var m1);
        metrics.AddFrame("clip1", "f2", 2, new[] { Detection("ALPHA_0NE"), Detection("ZZZZZZZZ") }, out var m2);   // fuzzy, wrong
        metrics.AddFrame("clip1", "f3", 3, Array.Empty<NameplateDetection>(), out _);
        metrics.AddFrame("clip1", "f4", 4, new[] { Detection("BRAVO-TWO"), Detection("ALPHA_ONE") }, out _);
        metrics.AddFrame("other", "g1", 1, new[] { Detection("ALPHA_ONE") }, out var m3);                        // clip without labels

        var g = metrics.Groups["clip1"];
        Assert.Equal(4, g.Frames);
        Assert.Equal(2, g.Labels);
        Assert.Equal(6, g.Detections);
        Assert.Equal(1, g.Excluded);
        Assert.Equal(1, g.Wrong);
        Assert.Equal(2, g.NamesExact);
        Assert.Equal(2, g.NamesFuzzy);
        Assert.Equal(3, g.ExactNameFrames);   // ALPHA_ONE in f1 and f4, BRAVO-TWO in f4
        Assert.Equal(4, g.FuzzyNameFrames);   // plus ALPHA_0NE in f2
        Assert.Equal(("ALPHA_ONE", true), m1[0]);
        Assert.Equal(("ALPHA_ONE", false), m2[0]);
        Assert.Null(m2[1].Label);
        Assert.Empty(m3);
        Assert.DoesNotContain("other", metrics.Groups.Keys);
        Assert.Contains("TOTAL", metrics.Format());
    }

    [Fact]
    public void Metrics_ForScreenshotsCountEachImage()
    {
        var labels = ClipLabels.ParseScreenshotCsv("file,expected_players\nshot1.jpg,ALPHA_ONE\nshot2.jpg,\n");
        var metrics = new ReplayMetrics(labels, Array.Empty<string>());

        metrics.AddFrame("Names", "shot1.jpg", 1, new[] { Detection("ALPHA_ONE") }, out _);
        metrics.AddFrame("Names", "shot2.jpg", 2, new[] { Detection("ALPHA_ONE") }, out _);   // not expected there: wrong

        Assert.Equal(1, metrics.Groups["shot1.jpg"].NamesExact);
        Assert.Equal(1, metrics.Groups["shot2.jpg"].Wrong);
        Assert.Equal(0, metrics.Groups["shot2.jpg"].Labels);
    }

    // ------------------------------------------------------------------------------------------------ files

    [Theory]
    [InlineData("f00041.jpg", 41)]
    [InlineData("clip1_000123.png", 123)]
    [InlineData("ScreenShot-2026-10-20_18-51-54-C92.jpg", 92)]
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
            foreach (var file in Directory.GetFiles(DetectorFixtures.Dir, "frame_*.png"))
                File.Copy(file, Path.Combine(frames, Path.GetFileName(file)));
            var labels = Path.Combine(root, "clips.txt");
            File.WriteAllText(labels, "synthetic: SYNTH_ALPHA, SYNTH_BRAVO, DUMMY_DELTA, FIXTURE_ECHO\n");

            var console = new StringWriter();
            var original = Console.Out;
            int exit;
            Console.SetOut(console);
            try
            {
                exit = Program.Main(new[] { frames, "--labels", labels, "--out", output, "--annotate" });
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal(0, exit);
            var lines = File.ReadAllLines(Path.Combine(output, "detections.csv"));
            Assert.StartsWith("source,frame,name,score,", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("synthetic/frame_a.png,") && l.Contains(",SYNTH_ALPHA,"));
            Assert.Contains(lines, l => l.StartsWith("synthetic/frame_b.png,") && l.Contains(",DUMMY_DELTA,"));
            Assert.DoesNotContain(lines, l => l.StartsWith("synthetic/frame_d.png,")); // stars only
            Assert.Contains("4 ", console.ToString()); // the 4 frames
            Assert.Contains("TOTAL", console.ToString());
            Assert.True(File.Exists(Path.Combine(output, "annotated", "synthetic", "frame_a.png")));
            Assert.False(File.Exists(Path.Combine(output, "annotated", "synthetic", "frame_d.png")));
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
