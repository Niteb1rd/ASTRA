using System.Text;
using Iris.Core.Storage;
using Xunit;

namespace Iris.Core.Tests;

public class FileChangeDetectorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<TimeSpan> _sleeps = new();
    private int _reads;

    public void Dispose() => _dir.Dispose();

    private string PathOf(string name = "watched.txt") => _dir.File(name);

    private static void Write(string path, string text, DateTime? time = null)
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
        if (time is { } t)
            File.SetLastWriteTimeUtc(path, t);
    }

    private static readonly DateTime T1 = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A detector that counts reads and records sleeps (so retries take no time).</summary>
    private FileChangeDetector Detector(string path) =>
        new(path, sleep: _sleeps.Add, readBytes: p =>
        {
            _reads++;
            return File.ReadAllBytes(p);
        });

    [Fact]
    public void FirstCheck_OfAnExistingFile_ReportsItsContent_AndTheSecondIsUnchanged()
    {
        var path = PathOf();
        Write(path, "hello\n", T1);
        var detector = Detector(path);

        var first = detector.Check();
        var second = detector.Check();

        Assert.Equal(FileChangeKind.Changed, first.Kind);
        Assert.Equal("hello\n", first.Text);
        Assert.Equal(FileChangeDetector.HashOf("hello\n"), first.Hash);
        Assert.Equal(FileChangeKind.Unchanged, second.Kind);
    }

    [Fact]
    public void UnchangedTimeAndLength_MeansNoReadAtAll()
    {
        var path = PathOf();
        Write(path, "hello\n", T1);
        var detector = Detector(path);
        detector.Check();
        Assert.Equal(1, _reads);

        detector.Check();
        detector.Check();

        Assert.Equal(1, _reads);
    }

    [Fact]
    public void TouchedButSameContent_IsUnchanged_AfterOneRead_AndTheNewTimeIsRemembered()
    {
        var path = PathOf();
        Write(path, "hello\n", T1);
        var detector = Detector(path);
        detector.Check();

        File.SetLastWriteTimeUtc(path, T1.AddMinutes(5));   // a save without changes, or a sync tool
        var touched = detector.Check();
        var again = detector.Check();

        Assert.Equal(FileChangeKind.Unchanged, touched.Kind);
        Assert.Equal(FileChangeKind.Unchanged, again.Kind);
        Assert.Equal(2, _reads);   // read once to compare; the second check saw the remembered new time
    }

    [Fact]
    public void AnOlderTimestampWithDifferentContent_IsChanged()
    {
        var path = PathOf();
        Write(path, "aaaa\n", T1);
        var detector = Detector(path);
        detector.Check();

        Write(path, "bbbb\n", T1.AddDays(-30));   // same length, restored from an older backup
        var change = detector.Check();

        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal("bbbb\n", change.Text);
    }

    [Fact]
    public void ADifferentLengthWithTheSameTime_IsChanged()
    {
        var path = PathOf();
        Write(path, "aaaa\n", T1);
        var detector = Detector(path);
        detector.Check();

        Write(path, "aaaa\nmore\n", T1);
        var change = detector.Check();

        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal("aaaa\nmore\n", change.Text);
    }

    [Fact]
    public void AChangedVersion_IsReportedOnlyOnce()
    {
        var path = PathOf();
        Write(path, "good\n", T1);
        var detector = Detector(path);
        detector.Check();

        Write(path, "broken ][\n", T1.AddMinutes(1));

        Assert.Equal(FileChangeKind.Changed, detector.Check().Kind);
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
        File.SetLastWriteTimeUtc(path, T1.AddMinutes(2));   // touched again, still the same broken text
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
    }

    [Fact]
    public void AByteOrderMark_IsDroppedFromTheText_ButCountsInTheHash()
    {
        var path = PathOf();
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' });
        var detector = Detector(path);

        var change = detector.Check();

        Assert.Equal("hi", change.Text);
        Assert.NotEqual(FileChangeDetector.HashOf("hi"), change.Hash);
    }

    // ------------------------------------------------------------------ locked files

    [Fact]
    public void ALockedFile_IsTriedFourTimes_100msApart_ThenGivenUpQuietly_AndNothingIsRemembered()
    {
        var path = PathOf();
        Write(path, "text\n", T1);
        var attempts = 0;
        var detector = new FileChangeDetector(path, _sleeps.Add, p =>
        {
            attempts++;
            throw new IOException("The process cannot access the file because it is being used by another process.");
        });

        var change = detector.Check();

        Assert.Equal(FileChangeKind.Locked, change.Kind);
        Assert.Equal(4, attempts);   // the first try and three retries
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100) }, _sleeps);
        Assert.Null(detector.KnownHash);
    }

    [Fact]
    public void ALockedFile_IsReadOnTheNextRound_WhenTheLockIsGone()
    {
        var path = PathOf();
        Write(path, "text\n", T1);
        var locked = true;
        var detector = new FileChangeDetector(path, _sleeps.Add, p => locked
            ? throw new IOException("in use")
            : File.ReadAllBytes(p));

        Assert.Equal(FileChangeKind.Locked, detector.Check().Kind);
        locked = false;
        var change = detector.Check();

        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal("text\n", change.Text);
    }

    [Fact]
    public void AFileThatIsLockedOnlyBriefly_IsReadWithinTheSameCheck()
    {
        var path = PathOf();
        Write(path, "text\n", T1);
        var failures = 2;
        var detector = new FileChangeDetector(path, _sleeps.Add, p =>
        {
            if (failures-- > 0)
                throw new IOException("in use");
            return File.ReadAllBytes(p);
        });

        var change = detector.Check();

        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal(2, _sleeps.Count);
    }

    [Fact]
    public void AccessDenied_IsTreatedLikeALock()
    {
        var path = PathOf();
        Write(path, "text\n", T1);
        var detector = new FileChangeDetector(path, _sleeps.Add, p => throw new UnauthorizedAccessException());

        Assert.Equal(FileChangeKind.Locked, detector.Check().Kind);
    }

    [Fact]
    public void ARealReadOfAnOpenFile_Works_BecauseTheFileIsOpenedShared()
    {
        var path = PathOf();
        Write(path, "text\n", T1);
        var detector = new FileChangeDetector(path);

        // an editor keeping the file open for reading and writing
        using var editor = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var change = detector.Check();

        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal("text\n", change.Text);
    }

    // ------------------------------------------------------------------ missing files

    [Fact]
    public void AMissingFile_IsReportedOnce()
    {
        var detector = Detector(PathOf("never-there.txt"));

        Assert.Equal(FileChangeKind.Missing, detector.Check().Kind);
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
    }

    [Fact]
    public void AFileThatDisappears_IsReportedOnce_AndComesBackQuietlyWhenTheContentIsTheSame()
    {
        var path = PathOf();
        Write(path, "same\n", T1);
        var detector = Detector(path);
        detector.Check();

        File.Delete(path);
        Assert.Equal(FileChangeKind.Missing, detector.Check().Kind);
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);

        Write(path, "same\n", T1.AddHours(1));
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
    }

    [Fact]
    public void AFileThatComesBackDifferent_IsChanged_AndAnotherDisappearanceIsReportedAgain()
    {
        var path = PathOf();
        Write(path, "one\n", T1);
        var detector = Detector(path);
        detector.Check();
        File.Delete(path);
        detector.Check();

        Write(path, "two\n", T1.AddHours(1));
        Assert.Equal(FileChangeKind.Changed, detector.Check().Kind);

        File.Delete(path);
        Assert.Equal(FileChangeKind.Missing, detector.Check().Kind);
    }

    // ------------------------------------------------------------------ IRIS's own writes

    [Fact]
    public void AnOwnSave_IsNeverSeenAsAnOutsideEdit()
    {
        var path = PathOf();
        Write(path, "old\n", T1);
        var detector = Detector(path);
        detector.Check();

        AtomicFile.WriteText(path, "mine\n", keepBackup: false);
        detector.RememberWritten("mine\n");
        var reads = _reads;

        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
        Assert.Equal(reads, _reads);   // not even read: time and length are the remembered ones
    }

    [Fact]
    public void AnOwnSave_ThatLooksDifferentOnDisk_IsStillRecognisedByItsContent()
    {
        var path = PathOf();
        Write(path, "old\n", T1);
        var detector = Detector(path);
        detector.Check();

        AtomicFile.WriteText(path, "mine\n", keepBackup: false);
        detector.RememberWritten("mine\n");
        File.SetLastWriteTimeUtc(path, T1.AddDays(-1));   // some tool resets the time

        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
    }

    [Fact]
    public void AnEditAfterAnOwnSave_IsChanged()
    {
        var path = PathOf();
        Write(path, "old\n", T1);
        var detector = Detector(path);
        detector.Check();
        AtomicFile.WriteText(path, "mine\n", keepBackup: false);
        detector.RememberWritten("mine\n");

        Write(path, "theirs\n", T1.AddMinutes(9));

        var change = detector.Check();
        Assert.Equal(FileChangeKind.Changed, change.Kind);
        Assert.Equal("theirs\n", change.Text);
    }

    [Fact]
    public void RememberFromDisk_TakesTheFileAsItIsNow()
    {
        var path = PathOf();
        Write(path, "old\n", T1);
        var detector = Detector(path);
        detector.Check();

        File.AppendAllText(path, "added by IRIS\n");
        detector.RememberFromDisk();

        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
    }

    [Fact]
    public void AnOwnSave_DuringARead_WinsOverTheStaleRead()
    {
        var path = PathOf();
        Write(path, "old\n", T1);
        FileChangeDetector? detector = null;
        detector = new FileChangeDetector(path, _sleeps.Add, p =>
        {
            var bytes = File.ReadAllBytes(p);
            detector!.RememberWritten("mine\n");   // IRIS saves while the check is reading
            return bytes;
        });

        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);
        Assert.Equal(FileChangeDetector.HashOf("mine\n"), detector.KnownHash);
    }

    // ------------------------------------------------------------------ loading

    [Fact]
    public void Load_ReadsTheFileEvenWhenItWasRemembered_AndRemembersAMissingFile()
    {
        var path = PathOf();
        Write(path, "text\n", T1);
        var detector = Detector(path);
        detector.Check();

        var loaded = detector.Load();
        Assert.Equal(FileChangeKind.Changed, loaded.Kind);
        Assert.Equal("text\n", loaded.Text);

        File.Delete(path);
        Assert.Equal(FileChangeKind.Missing, detector.Load().Kind);
        Assert.Equal(FileChangeKind.Unchanged, detector.Check().Kind);   // the missing file is already known
        Assert.Null(detector.KnownHash);
    }

    [Fact]
    public void EmptyPath_IsRejected() => Assert.Throws<ArgumentException>(() => new FileChangeDetector(""));
}
