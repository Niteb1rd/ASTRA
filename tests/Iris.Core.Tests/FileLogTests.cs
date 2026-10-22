using System.Globalization;
using System.Text;
using Iris.Core.Logging;
using Xunit;

namespace Iris.Core.Tests;

public class FileLogTests
{
    private static string DayName(DateTimeOffset utc) =>
        "iris-" + utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

    [Fact]
    public void Write_AppendsOneLineToTheDatedFile()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var log = new FileLog(dir.Path, clock);
        var t = clock.UtcNow;

        log.Write(t, "first");
        log.Write(t.AddSeconds(1), "second");

        var expectedPath = Path.Combine(dir.Path, DayName(t));
        Assert.Equal(expectedPath, log.CurrentFilePath);
        Assert.Equal(dir.Path, log.Folder);
        var bytes = File.ReadAllBytes(expectedPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // no BOM
        var lines = Encoding.UTF8.GetString(bytes).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  first", lines[0]);
        Assert.EndsWith("  second", lines[1]);
    }

    [Fact]
    public void Write_CreatesTheFolderIfMissing()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "a", "logs");
        var clock = new FakeClock();

        new FileLog(folder, clock).Write(clock.UtcNow, "hello");

        Assert.True(File.Exists(Path.Combine(folder, DayName(clock.UtcNow))));
    }

    [Fact]
    public void Write_AfterMidnight_GoesToANewFile()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var log = new FileLog(dir.Path, clock);
        var day1 = clock.UtcNow;
        var day2 = day1.AddDays(2);

        log.Write(day1, "one");
        log.Write(day2, "two");

        var file1 = Path.Combine(dir.Path, DayName(day1));
        var file2 = Path.Combine(dir.Path, DayName(day2));
        Assert.NotEqual(file1, file2);
        Assert.Contains("one", File.ReadAllText(file1));
        Assert.DoesNotContain("two", File.ReadAllText(file1));
        Assert.Contains("two", File.ReadAllText(file2));

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(file2, log.CurrentFilePath);
    }

    [Fact]
    public void Write_WorksWhileAnotherProgramHasTheFileOpenForReading()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var log = new FileLog(dir.Path, clock);
        log.Write(clock.UtcNow, "one");

        using (var reader = new FileStream(log.CurrentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            log.Write(clock.UtcNow, "two");
        }

        Assert.Null(log.LastError);
        Assert.Contains("two", File.ReadAllText(log.CurrentFilePath));
    }

    [Fact]
    public void Write_DoesNotThrow_WhenTheFolderPathIsAFile()
    {
        using var dir = new TempDir();
        var blocker = Path.Combine(dir.Path, "logs");
        File.WriteAllText(blocker, "I am a file, not a folder");
        var clock = new FakeClock();
        var log = new FileLog(blocker, clock);

        var ex = Record.Exception(() => log.Write(clock.UtcNow, "lost line"));

        Assert.Null(ex);
        Assert.NotNull(log.LastError);
    }

    [Fact]
    public void DeleteOlderThan_DeletesOnlyOldIrisLogs()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero).ToLocalTime();
        string Name(int daysAgo) => "iris-" + now.Date.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

        foreach (var name in new[]
                 {
                     Name(0), Name(29), Name(30), Name(31), Name(400),
                     "iris-notadate.log", "iris-2026-13-45.log", "other-2020-01-01.log", "iris-2020-01-01.txt", "notes.txt",
                 })
            File.WriteAllText(dir.File(name), "x");

        var deleted = FileLog.DeleteOlderThan(dir.Path, TimeSpan.FromDays(30), now);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(dir.File(Name(31))));
        Assert.False(File.Exists(dir.File(Name(400))));
        Assert.True(File.Exists(dir.File(Name(0))));
        Assert.True(File.Exists(dir.File(Name(29))));
        Assert.True(File.Exists(dir.File(Name(30))));
        Assert.True(File.Exists(dir.File("iris-notadate.log")));
        Assert.True(File.Exists(dir.File("iris-2026-13-45.log")));
        Assert.True(File.Exists(dir.File("other-2020-01-01.log")));
        Assert.True(File.Exists(dir.File("iris-2020-01-01.txt")));
        Assert.True(File.Exists(dir.File("notes.txt")));
    }

    [Fact]
    public void DeleteOlderThan_MissingFolder_ReturnsZero()
    {
        using var dir = new TempDir();

        Assert.Equal(0, FileLog.DeleteOlderThan(Path.Combine(dir.Path, "nope"), TimeSpan.FromDays(30), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TeeLog_WritesToAllTargets_EvenIfOneThrows()
    {
        var a = new ListLog();
        var b = new ListLog();
        var tee = new TeeLog(a, new ThrowingLog(), b);
        var t = DateTimeOffset.UtcNow;

        tee.Write(t, "hi");

        Assert.Single(a.Entries);
        Assert.Single(b.Entries);
        Assert.Equal("hi", b.Entries[0].Message);
    }

    private sealed class ThrowingLog : ILog
    {
        public void Write(DateTimeOffset utcTime, string message) => throw new InvalidOperationException("boom");
    }
}
