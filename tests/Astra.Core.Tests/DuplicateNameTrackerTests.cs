using Astra.Core.Cache;
using Astra.Core.Config;
using Astra.Core.Detection;
using Xunit;

namespace Astra.Core.Tests;

public class DuplicateNameTrackerTests : IDisposable
{
    private readonly CacheEnv _env = new();
    private readonly TempDir _dir = new();
    private readonly ListLog _log = new();
    private readonly string _path;
    private readonly ExclusionList _exclusions;
    private readonly SocialConfig _noPlayers = new();

    public DuplicateNameTrackerTests()
    {
        _path = _dir.File("exclusions.txt");
        File.WriteAllText(_path, "# test list\nStanton\n");
        _exclusions = ExclusionList.LoadOrCreate(_path, _env.Clock, _log);
    }

    public void Dispose()
    {
        _dir.Dispose();
        _env.Dispose();
    }

    private DuplicateNameTracker NewTracker() => new(_env.Db, _exclusions, _env.Clock);

    private static DetectedNameplate Plate(string name, int x, int y = 100) => new(name, x, y, 80, 20);

    private static IReadOnlyList<DetectedNameplate> Plates(params DetectedNameplate[] plates) => plates;

    // Two plates far apart (80 wide, 20 high) with the same name.
    private static IReadOnlyList<DetectedNameplate> TwoSeparate(string name) =>
        Plates(Plate(name, 0), Plate(name, 500));

    [Fact]
    public void TheSamePositionTwiceInOneFrame_DoesNotCount()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 100), Plate("Goldie", 100)), _noPlayers);

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void OverlappingAndTouchingRectangles_AreTheSameNameplate()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 100), Plate("Goldie", 150)), _noPlayers);   // overlap
        tracker.Process(2, Plates(Plate("Goldie", 100), Plate("Goldie", 180)), _noPlayers);   // touch: 100+80 = 180

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void ARectangleJustPastTheEdge_IsASeparateNameplate()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 100), Plate("Goldie", 181)), _noPlayers);

        Assert.Equal(1, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void ARectangleSeparateInHeightOnly_IsASeparateNameplate()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 100, 100), Plate("Goldie", 100, 300)), _noPlayers);

        Assert.Equal(1, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void ARepeatThatIsLinkedThroughAMiddleRectangle_IsStillOneNameplate()
    {
        var tracker = NewTracker();

        // A touches B, B touches C, A and C are apart: still one chain = one nameplate.
        tracker.Process(1, Plates(Plate("Goldie", 0), Plate("Goldie", 80), Plate("Goldie", 160)), _noPlayers);

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void TwoSeparatePositions_CountOneFrame_AndNamesMatchIgnoringCase()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 0), Plate("GOLDIE", 500)), _noPlayers);

        Assert.Equal(1, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void TheSameFrameIdTwice_CountsOnce()
    {
        var tracker = NewTracker();

        tracker.Process(7, TwoSeparate("Goldie"), _noPlayers);
        tracker.Process(7, TwoSeparate("Goldie"), _noPlayers);

        Assert.Equal(1, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void ManyNameplatesOfOneNameInOneFrame_CountOnce()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 0), Plate("Goldie", 300), Plate("Goldie", 600)), _noPlayers);

        Assert.Equal(1, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void ANameSeenOnlyOnce_InvalidNames_AndOtherNames_AreNotCounted()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 0), Plate("abc", 0), Plate("abc", 500), Plate("Nitebird", 300)), _noPlayers);

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
        Assert.Equal(0, _env.Db.GetDuplicateFrames("abc"));
        Assert.Equal(0, _env.Db.GetDuplicateFrames("Nitebird"));
    }

    [Fact]
    public void ThreeFrames_AddTheNameToTheFile_AndRemoveTheCount()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Process(1, TwoSeparate("Goldie"), _noPlayers));
        Assert.Empty(tracker.Process(2, TwoSeparate("Goldie"), _noPlayers));
        Assert.Equal(2, _env.Db.GetDuplicateFrames("Goldie"));
        Assert.False(_exclusions.IsExcluded("Goldie"));

        var added = tracker.Process(3, TwoSeparate("Goldie"), _noPlayers);

        Assert.Equal(new[] { "Goldie" }, added);
        Assert.True(_exclusions.IsExcluded("Goldie"));
        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
        Assert.Equal(
            "# test list\nStanton\n# auto-added 2026-01-01 UTC: seen on 2+ separate nameplates in 3 frames\nGoldie\n",
            File.ReadAllText(_path));
    }

    [Fact]
    public void AfterBeingExcluded_TheNameIsNoLongerCounted()
    {
        var tracker = NewTracker();
        for (int frame = 1; frame <= 3; frame++)
            tracker.Process(frame, TwoSeparate("Goldie"), _noPlayers);

        tracker.Process(4, TwoSeparate("Goldie"), _noPlayers);

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
    }

    [Fact]
    public void AlreadyExcludedNames_AreNeverCounted()
    {
        var tracker = NewTracker();

        tracker.Process(1, TwoSeparate("Stanton"), _noPlayers);

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Stanton"));
    }

    [Fact]
    public void PlayerListNames_AreNeverCountedOrAdded()
    {
        var tracker = NewTracker();
        var config = CacheEnv.ConfigWith(new[] { "Goldie" }, Array.Empty<string>());

        for (int frame = 1; frame <= 5; frame++)
            tracker.Process(frame, TwoSeparate("goldie"), config);

        Assert.Equal(0, _env.Db.GetDuplicateFrames("Goldie"));
        Assert.False(_exclusions.IsExcluded("Goldie"));
        Assert.Equal("# test list\nStanton\n", File.ReadAllText(_path));
    }

    [Fact]
    public void CountsSurviveANewTrackerOnTheSameDatabase_AcrossSessions()
    {
        NewTracker().Process(1, TwoSeparate("Goldie"), _noPlayers);
        NewTracker().Process(1, TwoSeparate("Goldie"), _noPlayers);   // a new session may reuse frame numbers

        Assert.Equal(2, _env.Db.GetDuplicateFrames("Goldie"));

        var added = NewTracker().Process(1, TwoSeparate("Goldie"), _noPlayers);

        Assert.Equal(new[] { "Goldie" }, added);
        Assert.True(_exclusions.IsExcluded("Goldie"));
    }

    [Fact]
    public void DifferentNamesAreCountedSeparately_InTheSameFrame()
    {
        var tracker = NewTracker();

        tracker.Process(1, Plates(Plate("Goldie", 0), Plate("Goldie", 500), Plate("Silvia", 0, 400), Plate("Silvia", 500, 400)), _noPlayers);

        Assert.Equal(1, _env.Db.GetDuplicateFrames("Goldie"));
        Assert.Equal(1, _env.Db.GetDuplicateFrames("Silvia"));
    }
}
