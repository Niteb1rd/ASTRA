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
        Assert.Equal(62, expected.Length);
        Assert.Equal(62, list.Count);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), list.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.True(list.IsExcluded("Stanton"));
        Assert.True(list.IsExcluded("stanton"));
        Assert.True(list.IsExcluded("AMMUNITON")); // a label of the game that the nameplate detector reads as a name (one letter off the word)
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

    [Fact]
    public void IsExcluded_IsExact_IgnoringOnlyTheCase()
    {
        var path = PathOf();
        WriteRaw(path, "Hurston\nLorville\n");
        var list = Load(path);

        Assert.True(list.IsExcluded("HURSTON"));
        Assert.True(list.IsExcluded("hurston"));
        Assert.True(list.IsExcluded(" Lorville "));
        Assert.False(list.IsExcluded("HURST0N"));     // 0 for O: no look-alike tolerance
        Assert.False(list.IsExcluded("L0RV1LLE"));
        Assert.False(list.IsExcluded("HURSTQN"));
        Assert.False(list.IsExcluded("LORVILLEX"));
    }

    [Fact]
    public void TheList_NeverWritesToTheFileAfterCreatingIt()
    {
        var path = PathOf();
        WriteRaw(path, "# my list\nGold\n");
        var list = Load(path);
        var before = File.ReadAllBytes(path);

        list.ReloadIfChanged();
        Assert.True(list.IsExcluded("Gold"));
        Assert.False(list.IsExcluded("Zinc"));

        Assert.Equal(before, File.ReadAllBytes(path)); // the duplicate rule excludes for the session only (SessionExclusions); nothing is appended
    }
}
