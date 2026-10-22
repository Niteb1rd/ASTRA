using Iris.Core.Config;
using Iris.Core.Storage;
using Xunit;

namespace Iris.Core.Tests;

public class DataPathsTests
{
    [Fact]
    public void Paths_AreInsideADataFolderNextToTheExe()
    {
        using var dir = new TempDir();

        var paths = DataPaths.FromBaseDirectory(dir.Path);

        Assert.Equal(Path.Combine(dir.Path, "Data"), paths.DataFolder);
        Assert.Equal(Path.Combine(dir.Path, "Data", "social-overlay.toml"), paths.SocialListFile);
        Assert.Equal(Path.Combine(dir.Path, "Data", "settings.toml"), paths.SettingsFile);
        Assert.Equal(Path.Combine(dir.Path, "Data", "cache.db"), paths.CacheFile);
        Assert.Equal(Path.Combine(dir.Path, "Data", "exclusions.txt"), paths.ExclusionsFile);
        Assert.Equal(Path.Combine(dir.Path, "Data", "logs"), paths.LogsFolder);
    }

    [Fact]
    public void FromBaseDirectory_CreatesNothing_EnsureCreatedCreatesFolders()
    {
        using var dir = new TempDir();

        var paths = DataPaths.FromBaseDirectory(dir.Path);
        Assert.False(Directory.Exists(paths.DataFolder));

        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.DataFolder));
        Assert.True(Directory.Exists(paths.LogsFolder));

        paths.EnsureCreated(); // second call is harmless
        Assert.True(Directory.Exists(paths.LogsFolder));
    }

    [Fact]
    public void EmptyBaseDirectory_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => DataPaths.FromBaseDirectory(""));
    }
}

public class SocialListStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static SocialConfig Sample()
    {
        var c = new SocialConfig { Name = "Squad" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));
        c.Orgs.Add(new OrgEntry("J0K3R5", 2, OverlayColor.Red));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 3, OverlayColor.Orange));
        return c;
    }

    [Fact]
    public void Load_MissingFile_GivesEmptyNewConfig()
    {
        var loaded = SocialListStore.Load(_dir.File("social-overlay.toml"));

        Assert.True(loaded.IsNew);
        Assert.True(loaded.IsValid);
        Assert.Empty(loaded.Errors);
        Assert.Empty(loaded.Config.Players);
        Assert.False(File.Exists(_dir.File("social-overlay.toml")));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = _dir.File("social-overlay.toml");

        var saved = SocialListStore.Save(path, Sample());
        var loaded = SocialListStore.Load(path);

        Assert.True(saved.Success, saved.Error);
        Assert.False(loaded.IsNew);
        Assert.True(loaded.IsValid);
        Assert.Equal("Squad", loaded.Config.Name);
        Assert.Equal(Sample().Players, loaded.Config.Players);
        Assert.Equal(Sample().Orgs, loaded.Config.Orgs);
        Assert.Equal(Sample().OrgRules, loaded.Config.OrgRules);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_CreatesMissingFolder()
    {
        var path = _dir.File(Path.Combine("a", "b", "list.toml"));

        Assert.True(SocialListStore.Save(path, Sample()).Success);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SecondSave_KeepsOneBackupOfThePreviousVersion()
    {
        var path = _dir.File("list.toml");
        SocialListStore.Save(path, Sample());
        var first = File.ReadAllText(path);

        var changed = Sample();
        changed.Players.Add(new PlayerEntry("TORIN_DEMPSEY", OverlayColor.Green));
        SocialListStore.Save(path, changed);
        var second = File.ReadAllText(path);

        Assert.NotEqual(first, second);
        Assert.Equal(first, File.ReadAllText(path + ".bak"));

        // a third save replaces the backup with the second version (still only one backup)
        SocialListStore.Save(path, Sample());
        Assert.Equal(second, File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FirstSave_HasNoBackup()
    {
        var path = _dir.File("list.toml");

        SocialListStore.Save(path, Sample());

        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Load_InvalidFile_ReturnsErrors_AndLeavesTheFileUntouched()
    {
        var path = _dir.File("list.toml");
        const string bad = "format = \"sc-social-overlay\"\nformat_version = 1\n\n[[players]]\nhandle = \"no\"\ncolor = \"Cyan\"\n";
        File.WriteAllText(path, bad);

        var loaded = SocialListStore.Load(path);

        Assert.False(loaded.IsNew);
        Assert.False(loaded.IsValid);
        Assert.NotEmpty(loaded.Errors);
        Assert.Equal(bad, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_BrokenToml_ReturnsErrors_AndAnEmptyConfig()
    {
        var path = _dir.File("list.toml");
        File.WriteAllText(path, "this is [[ not toml");

        var loaded = SocialListStore.Load(path);

        Assert.False(loaded.IsValid);
        Assert.NotEmpty(loaded.Errors);
        Assert.Empty(loaded.Config.Players);
    }

    [Fact]
    public void Load_UnknownKeys_AreSurfacedAsWarnings()
    {
        var path = _dir.File("list.toml");
        File.WriteAllText(path, "format = \"sc-social-overlay\"\nformat_version = 1\nfuture_thing = 5\n");

        var loaded = SocialListStore.Load(path);

        Assert.True(loaded.IsValid);
        Assert.Single(loaded.Warnings);
    }

    [Fact]
    public void FailedWrite_DoesNotBringAnOldBackupBack_WhenThereWasNoFile()
    {
        // The list file is gone (an import moved it away first) and an old .bak is lying around.
        // A write that fails before the swap must not copy that stale .bak into place and pretend nothing changed.
        var path = _dir.File("list.toml");
        File.WriteAllText(path + ".bak", "old backup");
        Directory.CreateDirectory(path + ".tmp"); // a directory where the temp file must go: writing it fails on every OS

        var result = SocialListStore.Save(path, Sample());

        Assert.False(result.Success);
        Assert.False(File.Exists(path));
        Assert.Equal("old backup", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void AtomicFile_FailedWrite_Throws_AndLeavesNoFileBehind()
    {
        var path = _dir.File("plain.toml");
        File.WriteAllText(path + ".bak", "old backup");
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteText(path, "new", keepBackup: true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AtomicFile_ReplacingAnExistingFile_KeepsTheBackup_OnlyWhenAsked()
    {
        var path = _dir.File("plain.toml");
        AtomicFile.WriteText(path, "one", keepBackup: true);
        AtomicFile.WriteText(path, "two", keepBackup: true);
        Assert.Equal("one", File.ReadAllText(path + ".bak"));

        var other = _dir.File("other.toml");
        AtomicFile.WriteText(other, "one", keepBackup: false);
        AtomicFile.WriteText(other, "two", keepBackup: false);
        Assert.Equal("two", File.ReadAllText(other));
        Assert.False(File.Exists(other + ".bak"));
    }

    [Fact]
    public void FailedSave_LeavesAnExistingFileAlone()
    {
        var path = _dir.File("list.toml");
        File.WriteAllText(path, "current");
        File.WriteAllText(path + ".bak", "older");
        Directory.CreateDirectory(path + ".tmp");

        var result = SocialListStore.Save(path, Sample());

        Assert.False(result.Success);
        Assert.Equal("current", File.ReadAllText(path));
    }

    [Fact]
    public void Save_InvalidConfig_IsRefused_AndDoesNotOverwrite()
    {
        var path = _dir.File("list.toml");
        SocialListStore.Save(path, Sample());
        var before = File.ReadAllText(path);

        var broken = new SocialConfig();
        broken.Players.Add(new PlayerEntry("no", OverlayColor.Red)); // too short for a handle

        var result = SocialListStore.Save(path, broken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
