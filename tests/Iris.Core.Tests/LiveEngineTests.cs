using Iris.Core.Cache;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Settings;
using Iris.Core.Social;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The tickrate setting, the org member lookup queue, and the whole live pipeline (fake frame source, detector, tracker, overlay model).</summary>
public class LiveEngineTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ----- nameplate_tracking_fps -----

    [Fact]
    public void TrackingFps_DefaultsTo30WithTheSpecifiedRange()
    {
        Assert.Equal(30, new AppSettings().NameplateTrackingFps);
        Assert.Equal(15, AppSettings.NameplateTrackingFpsMin);
        Assert.Equal(120, AppSettings.NameplateTrackingFpsMax);
        Assert.Equal(5, AppSettings.NameplateTrackingFpsStep);
        Assert.Equal(30, AppSettings.NameplateTrackingFpsDefault);
    }

    [Fact]
    public void TrackingFps_SaveThenLoadRoundTrips()
    {
        var path = _dir.File("settings.toml");
        var settings = new AppSettings { NameplateTrackingFps = 75 };

        var saved = AppSettingsToml.Save(path, settings);
        var loaded = AppSettingsToml.Load(path);

        Assert.True(saved.Success, saved.Error);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(75, loaded.Settings.NameplateTrackingFps);
        Assert.Contains("nameplate_tracking_fps = 75", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(120)]
    [InlineData(60)]
    public void TrackingFps_RangeLimitsAreAccepted(int fps)
    {
        var result = AppSettingsToml.Read($"nameplate_tracking_fps = {fps}\n");

        Assert.Empty(result.Warnings);
        Assert.Equal(fps, result.Settings.NameplateTrackingFps);
    }

    [Theory]
    [InlineData("nameplate_tracking_fps = 14")]
    [InlineData("nameplate_tracking_fps = 121")]
    [InlineData("nameplate_tracking_fps = 0")]
    [InlineData("nameplate_tracking_fps = -30")]
    [InlineData("nameplate_tracking_fps = \"60\"")]
    [InlineData("nameplate_tracking_fps = 45.5")]
    public void TrackingFps_OutOfRangeOrWrongTypeBecomesTheDefaultWithAWarning(string line)
    {
        var result = AppSettingsToml.Read("format_version = 1\n" + line + "\nsession_log_enabled = false\n");

        Assert.True(result.IsValid);
        Assert.StartsWith("nameplate_tracking_fps", Assert.Single(result.Warnings));
        Assert.Equal(30, result.Settings.NameplateTrackingFps);
        Assert.False(result.Settings.SessionLogEnabled);
    }

    [Fact]
    public void TrackingFps_IsAKnownKey_AndMissingGivesTheDefaultWithoutAWarning()
    {
        var known = AppSettingsToml.Read("nameplate_tracking_fps = 30\n");
        var missing = AppSettingsToml.Read("format_version = 1\n");

        Assert.Empty(known.Warnings);
        Assert.Empty(missing.Warnings);
        Assert.Equal(30, missing.Settings.NameplateTrackingFps);
    }

    // ----- CacheDatabase.IsOrgMember -----

    [Fact]
    public void IsOrgMember_ChecksTheCachedMemberList()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Jokers", new[] { "Alice", "Bobby" }, env.Start);

        Assert.True(env.Db.IsOrgMember("J0K3R5", "Alice"));
        Assert.True(env.Db.IsOrgMember("J0K3R5", "alice")); // handles ignore case
        Assert.False(env.Db.IsOrgMember("J0K3R5", "Carol"));
        Assert.False(env.Db.IsOrgMember("OTHER", "Alice"));
    }

    // ----- OrgMemberLookupQueue -----

    private sealed class QueueRig : IDisposable
    {
        public readonly CacheEnv Env = new();
        public readonly List<string> Lookups = new();
        public PlayerLookupStatus Answer = PlayerLookupStatus.Found;
        public readonly OrgMemberLookupQueue Queue;
        public readonly SocialConfig Config = new();

        public QueueRig()
        {
            Env.Db.SaveOrgListEntry("J0K3R5", "Jokers", new[] { "Alice", "Bobby" }, Env.Start);
            Config.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
            Queue = new OrgMemberLookupQueue(Env.Db, (handle, ct) =>
            {
                Lookups.Add(handle);
                return Task.FromResult(Answer switch
                {
                    PlayerLookupStatus.Found => PlayerLookup.Found(handle, Array.Empty<OrgMembership>(), false, false),
                    PlayerLookupStatus.NotFound => PlayerLookup.NotFound(handle, false),
                    _ => PlayerLookup.Failed(handle, "offline"),
                });
            }, Env.Clock, Env.Log);
        }

        public void Dispose() => Env.Dispose();
    }

    [Fact]
    public async Task Queue_AMemberWithoutAPlayerPageIsQueuedOnceAndLookedUp()
    {
        using var rig = new QueueRig();

        Assert.True(rig.Queue.Consider("Alice", rig.Config, 0));
        Assert.False(rig.Queue.Consider("Alice", rig.Config, 200)); // already waiting
        Assert.Equal(1, rig.Queue.PendingCount);

        Assert.True(await rig.Queue.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(new[] { "Alice" }, rig.Lookups);
        Assert.Equal(0, rig.Queue.PendingCount);
        Assert.False(await rig.Queue.ProcessNextAsync(CancellationToken.None)); // nothing waits
        Assert.False(rig.Queue.Consider("Alice", rig.Config, 100_000)); // found: never asked again by the queue
    }

    [Fact]
    public void Queue_NonMembersKnownPlayersAndNotFoundNamesAreNotQueued()
    {
        using var rig = new QueueRig();
        rig.Env.Db.SavePlayer(CacheEnv.Player("Bobby", CacheEnv.MainOrg("J0K3R5")), CacheSource.Hotkey, rig.Env.Start);
        rig.Env.Db.SaveNotFound(NotFoundKind.Player, "Alice", CacheSource.Hotkey, rig.Env.Start);

        Assert.False(rig.Queue.Consider("Carol", rig.Config, 0));  // not in the org
        Assert.False(rig.Queue.Consider("Bobby", rig.Config, 0));  // page already cached
        Assert.False(rig.Queue.Consider("Alice", rig.Config, 0));  // RSI said there is no such citizen
        Assert.False(rig.Queue.Consider("x", rig.Config, 0));      // not a valid handle
        Assert.False(rig.Queue.Consider("Alice", new SocialConfig(), 0)); // no org list at all
        Assert.Equal(0, rig.Queue.PendingCount);
    }

    [Fact]
    public async Task Queue_AFailedLookupIsAskedAgainOnlyAfterTheRecheckTime()
    {
        using var rig = new QueueRig();
        rig.Answer = PlayerLookupStatus.Failed;

        Assert.True(rig.Queue.Consider("Alice", rig.Config, 0));
        await rig.Queue.ProcessNextAsync(CancellationToken.None);
        Assert.False(rig.Queue.Consider("Alice", rig.Config, OrgMemberLookupQueue.RecheckMs - 1));
        Assert.True(rig.Queue.Consider("Alice", rig.Config, OrgMemberLookupQueue.RecheckMs));
        Assert.Equal(1, rig.Queue.PendingCount);
    }

    [Fact]
    public void Queue_ClearForgetsWhatWaitsAndAllowsItToBeQueuedAgain()
    {
        using var rig = new QueueRig();
        Assert.True(rig.Queue.Consider("Alice", rig.Config, 0));

        rig.Queue.Clear();

        Assert.Equal(0, rig.Queue.PendingCount);
        Assert.True(rig.Queue.Consider("Alice", rig.Config, OrgMemberLookupQueue.RecheckMs)); // asked again after the recheck time
    }

    [Fact]
    public void Queue_HoldsAtMostCapacityNames()
    {
        using var rig = new QueueRig();
        var names = Enumerable.Range(0, OrgMemberLookupQueue.Capacity + 5).Select(i => "Member" + i).ToList();
        rig.Env.Db.SaveOrgListEntry("J0K3R5", "Jokers", names, rig.Env.Start);

        var queued = names.Count(n => rig.Queue.Consider(n, rig.Config, 0));

        Assert.Equal(OrgMemberLookupQueue.Capacity, queued);
        Assert.Equal(OrgMemberLookupQueue.Capacity, rig.Queue.PendingCount);
    }

    [Fact]
    public async Task Queue_ACancelledLookupIsForgotten()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Jokers", new[] { "Alice" }, env.Start);
        var config = new SocialConfig();
        config.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        var queue = new OrgMemberLookupQueue(env.Db, (_, ct) => Task.FromException<PlayerLookup>(new OperationCanceledException(ct)), env.Clock);
        Assert.True(queue.Consider("Alice", config, 0));

        await Assert.ThrowsAsync<OperationCanceledException>(() => queue.ProcessNextAsync(CancellationToken.None));

        Assert.True(queue.Consider("Alice", config, OrgMemberLookupQueue.RecheckMs)); // not remembered as requested
    }

    // ----- the pipeline: fake frame source -> detector -> tracker -> overlay model -----

    /// <summary>Serves the screen region that is asked for out of one image, as if the image were the game window at <see cref="Origin"/> on the screen.</summary>
    private sealed class ImageFrameSource : IFrameSource
    {
        private Mat _window;
        private Mat? _last;

        public ImageFrameSource(Mat window, int originX, int originY)
        {
            _window = window;
            Origin = new Point(originX, originY);
        }

        public Point Origin { get; }
        public int Grabs { get; private set; }
        public int Releases { get; private set; }
        public bool Unavailable { get; set; }
        public Rect LastRegion { get; private set; }

        public void Show(Mat window) => _window = window;

        public GrabResult Grab(Rect screenRegion)
        {
            Grabs++;
            LastRegion = screenRegion;
            if (Unavailable)
                return GrabResult.Unavailable("test: no picture");
            var crop = new Rect(screenRegion.X - Origin.X, screenRegion.Y - Origin.Y, screenRegion.Width, screenRegion.Height);
            _last?.Dispose();
            _last = new Mat(_window, crop).Clone();
            return new GrabResult(GrabStatus.Ok, _last);
        }

        public void Release() => Releases++;

        public void Dispose() => _last?.Dispose();
    }

    private const int WindowX = 100;
    private const int WindowY = 50;

    private static DetectorFixtureFrame Frame(string file) => DetectorFixtures.Load().Single(f => f.File == file);

    private LiveDetectionEngine NewEngine(out ExclusionList exclusions, Func<string, IReadOnlyList<OrgMembership>?>? orgs = null, IClock? clock = null)
    {
        var testClock = clock ?? new FakeClock();
        exclusions = ExclusionList.LoadOrCreate(_dir.File("exclusions.txt"), testClock, new ListLog());
        return new LiveDetectionEngine(new NameplateReader(LetterModel.LoadEmbedded()), exclusions, orgs ?? (_ => null));
    }

    private static PixelRect Window(DetectorFixtureFrame frame) => new(WindowX, WindowY, frame.Width, frame.Height);

    [OpenCvFact]
    public void Pipeline_AConfirmedNameplateGetsABracketAtItsPlaceInTheWindow()
    {
        var frame = Frame("frame_c.png");
        var expected = Assert.Single(frame.Default.Detections);
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var source = new ImageFrameSource(image, WindowX, WindowY);
        using var engine = NewEngine(out _);
        var config = new SocialConfig();

        // reads every 200 ms: the name is confirmed on the third (3 identical reads over at least 300 ms)
        var first = engine.Step(source, Window(frame), config, 1000);
        Assert.Equal(StepStatus.Read, first.Status);
        Assert.Empty(first.Snapshot.Brackets);
        Assert.Empty(engine.Step(source, Window(frame), config, 1200).Snapshot.Brackets);
        var third = engine.Step(source, Window(frame), config, 1400);

        var bracket = Assert.Single(third.Snapshot.Brackets);
        Assert.Equal(expected.Name, bracket.Handle);
        Assert.Equal(MatchKind.NoMatch, bracket.Kind);
        var left = Math.Min(expected.NameBox.X, expected.DistanceBox.X) + WindowX;
        var top = Math.Min(expected.NameBox.Y, expected.DistanceBox.Y) + WindowY;
        var right = Math.Max(expected.NameBox.Right, expected.DistanceBox.Right) + WindowX;
        var bottom = Math.Max(expected.NameBox.Bottom, expected.DistanceBox.Bottom) + WindowY;
        Assert.InRange(bracket.Box.X, left - 2, left + 2);
        Assert.InRange(bracket.Box.Y, top - 2, top + 2);
        Assert.InRange(bracket.Box.Right, right - 2, right + 2);
        Assert.InRange(bracket.Box.Bottom, bottom - 2, bottom + 2);
        var entry = Assert.Single(third.Snapshot.Panel);
        Assert.Equal(expected.Name, entry.Handle);
        Assert.Equal(PanelText.NotLookedUp, entry.OrgsLine);

        // only the scan region (and its context) was asked for, in screen pixels
        Assert.Equal(engine.Geometry!.ToScreen(WindowX, WindowY), source.LastRegion);
        Assert.True((long)source.LastRegion.Width * source.LastRegion.Height < (long)frame.Width * frame.Height);
    }

    [OpenCvFact]
    public void Pipeline_ThePlayerListColorsTheBracketAndACorrectionOfTheListSwitchesItAtOnce()
    {
        var frame = Frame("frame_c.png");
        var name = Assert.Single(frame.Default.Detections).Name;
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var source = new ImageFrameSource(image, WindowX, WindowY);
        using var engine = NewEngine(out _);
        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry(name, OverlayColor.Cyan));

        engine.Step(source, Window(frame), config, 1000);
        engine.Step(source, Window(frame), config, 1200);
        var third = engine.Step(source, Window(frame), config, 1400);

        var bracket = Assert.Single(third.Snapshot.Brackets);
        Assert.Equal(MatchKind.PlayerList, bracket.Kind);
        Assert.Equal(OverlayColor.Cyan, bracket.Color);
        Assert.Equal(MatchKind.PlayerList, Assert.Single(third.Snapshot.Panel).Kind);

        // the user takes the player off the list: the next read shows it as No Match
        config.Players.Clear();
        var next = engine.Step(source, Window(frame), config, 1600);
        Assert.Equal(MatchKind.NoMatch, Assert.Single(next.Snapshot.Brackets).Kind);
    }

    [OpenCvFact]
    public void Pipeline_AnExcludedNameGetsNoBracket()
    {
        var frame = Frame("frame_c.png");
        var name = Assert.Single(frame.Default.Detections).Name;
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var source = new ImageFrameSource(image, WindowX, WindowY);
        using var engine = NewEngine(out var exclusions);
        Assert.True(exclusions.AddAutomatic(name, "test"));
        var config = new SocialConfig();

        engine.Step(source, Window(frame), config, 1000);
        engine.Step(source, Window(frame), config, 1200);
        var third = engine.Step(source, Window(frame), config, 1400);

        Assert.Empty(third.Snapshot.Brackets);
        Assert.Empty(third.Snapshot.Panel);
        Assert.Single(engine.Tracker.Tracks); // the plate is tracked, it is just not drawn
    }

    [OpenCvFact]
    public void Pipeline_WhenThePlateGoesTheBracketIsHiddenThenTheTrackExpires()
    {
        var shown = Frame("frame_c.png");
        var empty = Frame("frame_d.png");
        using var withPlate = Cv2.ImRead(shown.ImagePath, ImreadModes.Color);
        using var withoutPlate = Cv2.ImRead(empty.ImagePath, ImreadModes.Color);
        Assert.Equal(withPlate.Size(), withoutPlate.Size()); // the fixtures used here are the same size
        using var source = new ImageFrameSource(withPlate, WindowX, WindowY);
        using var engine = NewEngine(out _);
        var config = new SocialConfig();
        var window = Window(shown);

        engine.Step(source, window, config, 1000);
        engine.Step(source, window, config, 1200);
        Assert.Single(engine.Step(source, window, config, 1400).Snapshot.Brackets);

        source.Show(withoutPlate); // the plate was last seen at 1400
        Assert.Single(engine.Step(source, window, config, 1600).Snapshot.Brackets); // 200 ms since seen
        Assert.Single(engine.Step(source, window, config, 1800).Snapshot.Brackets); // 400 ms
        Assert.Single(engine.Step(source, window, config, 2000).Snapshot.Brackets); // 600 ms: the limit, not over it
        Assert.Empty(engine.Step(source, window, config, 2200).Snapshot.Brackets);  // 800 ms: hidden
        Assert.NotEmpty(engine.Tracker.Tracks);                                     // the tracker keeps the track a little longer (1 s)
        Assert.Empty(engine.Step(source, window, config, 2600).Snapshot.Brackets);
        Assert.Empty(engine.Tracker.Tracks);                                        // 1200 ms since seen: expired
    }

    [OpenCvFact]
    public void Pipeline_AnUnavailableFrameSkipsTheReadAndKeepsTheLastSnapshot()
    {
        var frame = Frame("frame_c.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var source = new ImageFrameSource(image, WindowX, WindowY);
        using var engine = NewEngine(out _);
        var config = new SocialConfig();
        engine.Step(source, Window(frame), config, 1000);
        engine.Step(source, Window(frame), config, 1200);
        var confirmed = engine.Step(source, Window(frame), config, 1400);

        source.Unavailable = true;
        var skipped = engine.Step(source, Window(frame), config, 1600);

        Assert.Equal(StepStatus.Skipped, skipped.Status);
        Assert.Equal("test: no picture", skipped.Message);
        Assert.Same(confirmed.Snapshot, skipped.Snapshot);
    }

    [OpenCvFact]
    public void Pipeline_ResetForgetsTheTracksAndTheSnapshot()
    {
        var frame = Frame("frame_c.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var source = new ImageFrameSource(image, WindowX, WindowY);
        using var engine = NewEngine(out _);
        var config = new SocialConfig();
        engine.Step(source, Window(frame), config, 1000);
        engine.Step(source, Window(frame), config, 1200);
        Assert.Single(engine.Step(source, Window(frame), config, 1400).Snapshot.Brackets);

        engine.Reset();

        Assert.Empty(engine.Tracker.Tracks);
        Assert.Same(LiveSnapshot.Empty, engine.Snapshot);
    }

    [OpenCvFact]
    public void Pipeline_ATooSmallWindowIsSkipped()
    {
        using var image = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(0));
        using var source = new ImageFrameSource(image, 0, 0);
        using var engine = NewEngine(out _);

        var result = engine.Step(source, new PixelRect(0, 0, 1, 1), new SocialConfig(), 1000);

        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Equal(0, source.Grabs);
    }

    [OpenCvFact]
    public void Pipeline_ThePredictedBracketBetweenReadsFollowsTheTrack()
    {
        // the plate stands still: ticks between two reads give the same model instance (no redraw), and the model is the snapshot's
        var frame = Frame("frame_c.png");
        using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
        using var source = new ImageFrameSource(image, WindowX, WindowY);
        using var engine = NewEngine(out _);
        var config = new SocialConfig();
        engine.Step(source, Window(frame), config, 1000);
        engine.Step(source, Window(frame), config, 1200);
        var snapshot = engine.Step(source, Window(frame), config, 1400).Snapshot;

        var a = LivePredictor.ModelAt(snapshot, 1400, LiveSettings.Default);
        var b = LivePredictor.ModelAt(snapshot, 1433, LiveSettings.Default, a);
        var c = LivePredictor.ModelAt(snapshot, 1466, LiveSettings.Default, b);

        Assert.Same(a, b);
        Assert.Same(a, c);
        Assert.Equal(snapshot.Brackets[0].Box, a.Brackets[0].Box);
        Assert.Same(OverlayModel.Empty, LivePredictor.ModelAt(snapshot, 1400 + 601, LiveSettings.Default, c));
    }

    [OpenCvFact]
    public void Pipeline_EveryPlateOfTheFixtureFramesIsFoundThroughTheCrop_AtBothFrameSizes()
    {
        // frame_a and frame_f are 1440 pixels high, frame_b 1080: the pixel constants are scaled with the game window's height, not the crop's
        foreach (var file in new[] { "frame_a.png", "frame_b.png", "frame_f.png" })
        {
            var frame = Frame(file);
            using var image = Cv2.ImRead(frame.ImagePath, ImreadModes.Color);
            using var source = new ImageFrameSource(image, WindowX, WindowY);
            using var engine = NewEngine(out _);
            var config = new SocialConfig();

            engine.Step(source, Window(frame), config, 1000);
            engine.Step(source, Window(frame), config, 1200);
            var third = engine.Step(source, Window(frame), config, 1400);

            var found = third.Snapshot.Brackets.Select(b => b.Handle).ToHashSet(StringComparer.Ordinal);
            foreach (var detection in frame.Default.Detections)
                Assert.True(found.Contains(detection.Name), $"{file}: {detection.Name} was not found; found: {string.Join(", ", found)}");
        }
    }
}
