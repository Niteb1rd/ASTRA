using System.Text;
using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>A folder in the system temp directory that is deleted when the test ends.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iris-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ExclusionListTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string PathOf(string name = "exclusions.txt") => _dir.File(name);

    private ExclusionList Load(string path) => ExclusionList.LoadOrCreate(path, _clock, _log);

    private static string[] DefaultNames() =>
        ExclusionList.ReadDefaultText()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

    private static void WriteRaw(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    [Fact]
    public void MissingFile_IsCreatedFromTheDefaults_WithItsFolder()
    {
        var path = _dir.File(System.IO.Path.Combine("sub", "folder", "exclusions.txt"));

        var list = Load(path);

        Assert.True(File.Exists(path));
        var expected = DefaultNames();
        Assert.Equal(61, expected.Length);
        Assert.Equal(61, list.Count);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), list.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.True(list.IsExcluded("Stanton"));
        Assert.True(list.IsExcluded("stanton"));
    }

    [Fact]
    public void CreatedFile_HasNoByteOrderMark()
    {
        var path = PathOf();
        Load(path);

        var bytes = File.ReadAllBytes(path);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void ExistingFile_IsNotOverwritten()
    {
        var path = PathOf();
        WriteRaw(path, "OnlyMe\n");

        var list = Load(path);

        Assert.Equal(1, list.Count);
        Assert.Equal("OnlyMe\n", File.ReadAllText(path));
    }

    [Fact]
    public void CommentsBlankLinesAndSpaces_AreHandled_AndMatchingIgnoresCase()
    {
        var path = PathOf();
        WriteRaw(path, "# a comment\n\n   \n  Gold  \r\n#Silver\nCopper\n");

        var list = Load(path);

        Assert.Equal(2, list.Count);
        Assert.True(list.IsExcluded("gold"));
        Assert.True(list.IsExcluded("GOLD"));
        Assert.True(list.IsExcluded(" Gold "));
        Assert.True(list.IsExcluded("copper"));
        Assert.False(list.IsExcluded("Silver"));
        Assert.False(list.IsExcluded("#Silver"));
        Assert.False(list.IsExcluded(""));
    }

    [Fact]
    public void ReloadIfChanged_PicksUpAHandEdit()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);
        Assert.False(list.IsExcluded("Copper"));

        WriteRaw(path, "Gold\nCopper\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        list.ReloadIfChanged();

        Assert.True(list.IsExcluded("Copper"));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void ReloadIfChanged_DoesNotReadAnUnchangedFile()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);

        list.ReloadIfChanged();
        list.ReloadIfChanged();

        Assert.Equal(1, list.Count);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("could not"));
    }

    [Fact]
    public void ReloadIfChanged_KeepsTheOldListWhenTheFileIsMissing_AndLogsOnce()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);
        var before = _log.Entries.Count;
        File.Delete(path);

        list.ReloadIfChanged();
        list.ReloadIfChanged();
        list.ReloadIfChanged();

        Assert.True(list.IsExcluded("Gold"));
        Assert.Equal(before + 1, _log.Entries.Count);
    }

    [Fact]
    public void AddAutomatic_AppendsCommentAndName_AndKeepsTheUsersContentExactly()
    {
        var path = PathOf();
        const string original = "# mine\nGold\n\n  Copper  \n";
        WriteRaw(path, original);
        var list = Load(path);

        var added = list.AddAutomatic("Zinc", "seen twice");

        Assert.True(added);
        Assert.Equal(original + "# auto-added 2026-01-01 UTC: seen twice\nZinc\n", File.ReadAllText(path));
        Assert.True(list.IsExcluded("zinc"));
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void AddAutomatic_AddsANewlineFirstWhenTheFileEndsWithoutOne()
    {
        var path = PathOf();
        WriteRaw(path, "Gold");
        var list = Load(path);

        Assert.True(list.AddAutomatic("Zinc", "why"));

        Assert.Equal("Gold\n# auto-added 2026-01-01 UTC: why\nZinc\n", File.ReadAllText(path));
        var reloaded = Load(path);
        Assert.Equal(2, reloaded.Count);
        Assert.True(reloaded.IsExcluded("Gold"));
        Assert.True(reloaded.IsExcluded("Zinc"));
    }

    [Fact]
    public void AddAutomatic_WorksOnAnEmptyFile_AndWritesNoByteOrderMark()
    {
        var path = PathOf();
        WriteRaw(path, "");
        var list = Load(path);

        Assert.True(list.AddAutomatic("Zinc", "why"));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("# auto-added 2026-01-01 UTC: why\nZinc\n", Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void AddAutomatic_RefusesDuplicates_IgnoringCase_AndWritesNothing()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);

        Assert.False(list.AddAutomatic("GOLD", "again"));
        Assert.True(list.AddAutomatic("Zinc", "first"));
        Assert.False(list.AddAutomatic("zinc", "second"));

        Assert.Equal("Gold\n# auto-added 2026-01-01 UTC: first\nZinc\n", File.ReadAllText(path));
    }

    [Fact]
    public void AddAutomatic_NoticesANameTheUserJustAddedByHand()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);
        WriteRaw(path, "Gold\nZinc\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Assert.False(list.AddAutomatic("Zinc", "why"));

        Assert.Equal("Gold\nZinc\n", File.ReadAllText(path));
    }

    [Fact]
    public void AddAutomatic_UsesTheClockDate()
    {
        var path = PathOf();
        var list = Load(path);
        _clock.UtcNow = new DateTimeOffset(2027, 3, 9, 23, 59, 0, TimeSpan.Zero);

        Assert.True(list.AddAutomatic("Zincite", "why"));

        Assert.EndsWith("# auto-added 2027-03-09 UTC: why\nZincite\n", File.ReadAllText(path));
    }

    [Fact]
    public void AddAutomatic_IsNotReportedAsAChange_ButALaterOutsideEditIs()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);

        Assert.True(list.AddAutomatic("Zinc", "why"));
        Assert.False(list.ReloadIfChanged());   // our own append

        WriteRaw(path, "Gold\nZinc\nLead and more\n");
        Assert.True(list.ReloadIfChanged());
        Assert.True(list.IsExcluded("Lead and more"));
        Assert.False(list.ReloadIfChanged());   // reported once
    }

    [Fact]
    public void AddAutomatic_DoesNotSwallowTheNotificationOfAnOutsideChangeItNoticed()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);
        WriteRaw(path, "Gold\nCopper\n");   // edited by hand; nobody has looked yet

        Assert.True(list.AddAutomatic("Zinc", "why"));   // looks at the file first, and finds Copper

        Assert.True(list.IsExcluded("Copper"));
        Assert.True(list.IsExcluded("Zinc"));
        Assert.True(list.ReloadIfChanged());    // the poll still reports the hand edit
        Assert.False(list.ReloadIfChanged());
    }

    [Fact]
    public void ReloadIfChanged_ReportsAChangeOnlyOnce_AndNotTheFirstLoad()
    {
        var path = PathOf();
        WriteRaw(path, "Gold\n");
        var list = Load(path);

        Assert.False(list.ReloadIfChanged());   // LoadOrCreate already took the first load

        WriteRaw(path, "Gold\nCopper\n");
        Assert.True(list.ReloadIfChanged());
        Assert.False(list.ReloadIfChanged());
    }
}
