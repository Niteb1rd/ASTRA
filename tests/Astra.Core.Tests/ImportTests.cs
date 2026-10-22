using System.Globalization;
using Astra.Core.Config;
using Astra.Core.Storage;
using Xunit;

namespace Astra.Core.Tests;

public class SocialConfigMergerTests
{
    private static SocialConfig Original()
    {
        var c = new SocialConfig { Name = "Mine" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));
        c.Players.Add(new PlayerEntry("Alpha_One", OverlayColor.Red));
        c.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 2, OverlayColor.Orange));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 3, OverlayColor.Purple));
        return c;
    }

    private static SocialConfig Imported()
    {
        var c = new SocialConfig { Name = "Theirs" };
        c.Players.Add(new PlayerEntry("NITEBIRD", OverlayColor.Green));   // duplicate of Nitebird (case differs)
        c.Players.Add(new PlayerEntry("Zed_Zed", OverlayColor.Blue));     // new
        c.Orgs.Add(new OrgEntry("j0k3r5", 9, OverlayColor.Blue));         // duplicate
        c.Orgs.Add(new OrgEntry("HEKATE", 4, OverlayColor.Yellow));       // new
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "pmc", 9, OverlayColor.Blue)); // duplicate
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Activity, "PMC", 5, OverlayColor.Green)); // other kind: new
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 1, OverlayColor.Cyan));   // duplicate by kind only
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoOrg, null, 6, OverlayColor.Magenta));   // new
        return c;
    }

    [Fact]
    public void Merge_OriginalRowsFirst_ThenNewImportedRowsInImportedOrder()
    {
        var merged = SocialConfigMerger.Merge(Original(), Imported());

        Assert.Equal(new[] { "Nitebird", "Alpha_One", "Zed_Zed" }, merged.Config.Players.Select(p => p.Handle));
        Assert.Equal(new[] { "J0K3R5", "HEKATE" }, merged.Config.Orgs.Select(o => o.Sid));
        Assert.Equal(
            new[] { AttributeKind.Archetype, AttributeKind.Redacted, AttributeKind.Activity, AttributeKind.NoOrg },
            merged.Config.OrgRules.Select(r => r.Kind));
    }

    [Fact]
    public void Merge_OriginalWins_ColorAndPriorityKept()
    {
        var merged = SocialConfigMerger.Merge(Original(), Imported());

        Assert.Equal(OverlayColor.Cyan, merged.Config.Players.Single(p => p.Handle == "Nitebird").Color);
        var org = merged.Config.Orgs.Single(o => o.Sid == "J0K3R5");
        Assert.Equal(1, org.Priority);
        Assert.Equal(OverlayColor.Red, org.Color);
        var archetype = merged.Config.OrgRules.Single(r => r.Kind == AttributeKind.Archetype);
        Assert.Equal(2, archetype.Priority);
        Assert.Equal(OverlayColor.Orange, archetype.Color);
        Assert.Equal(3, merged.Config.OrgRules.Single(r => r.Kind == AttributeKind.Redacted).Priority);
    }

    [Fact]
    public void Merge_CountsAddedAndSkipped()
    {
        var merged = SocialConfigMerger.Merge(Original(), Imported());

        Assert.Equal(1, merged.AddedPlayers);
        Assert.Equal(1, merged.AddedOrgs);
        Assert.Equal(2, merged.AddedRules);
        Assert.Equal(4, merged.AddedTotal);
        Assert.Equal(1, merged.SkippedPlayers);
        Assert.Equal(1, merged.SkippedOrgs);
        Assert.Equal(2, merged.SkippedRules);
        Assert.Equal(4, merged.SkippedDuplicates);
    }

    [Fact]
    public void Merge_KeepsTheOriginalListName()
    {
        Assert.Equal("Mine", SocialConfigMerger.Merge(Original(), Imported()).Config.Name);
    }

    [Fact]
    public void Merge_IntoEmptyOriginal_TakesEverything()
    {
        var merged = SocialConfigMerger.Merge(new SocialConfig(), Imported());

        Assert.Equal(2, merged.Config.Players.Count);
        Assert.Equal(2, merged.Config.Orgs.Count);
        Assert.Equal(4, merged.Config.OrgRules.Count);
        Assert.Equal(0, merged.SkippedDuplicates);
        Assert.Null(merged.Config.Name);
    }

    [Fact]
    public void Merge_DoesNotChangeItsInputs()
    {
        var original = Original();
        var imported = Imported();

        SocialConfigMerger.Merge(original, imported);

        Assert.Equal(2, original.Players.Count);
        Assert.Equal(2, imported.Players.Count);
    }

    [Fact]
    public void Merge_DuplicateInsideTheImportedFile_IsAddedOnlyOnce()
    {
        var imported = new SocialConfig();
        imported.OrgRules.Add(new OrgAttributeRule(AttributeKind.Activity, "Piracy", 1, OverlayColor.Red));
        imported.OrgRules.Add(new OrgAttributeRule(AttributeKind.Activity, "piracy", 2, OverlayColor.Blue));

        var merged = SocialConfigMerger.Merge(new SocialConfig(), imported);

        Assert.Single(merged.Config.OrgRules);
        Assert.Equal(1, merged.SkippedRules);
        Assert.Equal(OverlayColor.Red, merged.Config.OrgRules[0].Color);
    }
}

public class ConfigImporterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private string ListPath => _dir.File("social-overlay.toml");

    private static SocialConfig Mine()
    {
        var c = new SocialConfig { Name = "Mine" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));
        c.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        return c;
    }

    private static SocialConfig Theirs()
    {
        var c = new SocialConfig { Name = "Theirs" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Green));
        c.Players.Add(new PlayerEntry("Zed_Zed", OverlayColor.Blue));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoOrg, null, 4, OverlayColor.Purple));
        return c;
    }

    private string Backup()
    {
        var stamp = _clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return _dir.File($"social-overlay.backup-{stamp}.toml");
    }

    [Fact]
    public void BackupMode_RenamesTheOldFile_ThenWritesTheImportedOne()
    {
        SocialListStore.Save(ListPath, Mine());
        var oldText = File.ReadAllText(ListPath);

        var result = ConfigImporter.Import(ListPath, Theirs(), ImportMode.BackupExisting, _clock);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Backup(), result.BackupPath);
        Assert.Equal(oldText, File.ReadAllText(Backup()));
        var now = SocialListStore.Load(ListPath);
        Assert.Equal("Theirs", now.Config.Name);
        Assert.Equal(2, now.Config.Players.Count);
        Assert.Equal(OverlayColor.Green, now.Config.Players[0].Color);
        Assert.Equal(2, result.PlayersAdded);
        Assert.Equal(0, result.OrgsAdded);
        Assert.Equal(1, result.RulesAdded);
        Assert.Contains(Path.GetFileName(Backup()), result.Summary);
    }

    [Fact]
    public void BackupMode_NameIsMadeUnique_WhenTwoImportsShareASecond()
    {
        SocialListStore.Save(ListPath, Mine());
        ConfigImporter.Import(ListPath, Theirs(), ImportMode.BackupExisting, _clock);

        var second = ConfigImporter.Import(ListPath, Mine(), ImportMode.BackupExisting, _clock);

        Assert.True(second.Success, second.Error);
        Assert.NotEqual(Backup(), second.BackupPath);
        Assert.True(File.Exists(Backup()));
        Assert.True(File.Exists(second.BackupPath));
    }

    [Fact]
    public void MergeMode_WritesTheMergedResult_OriginalWins_AndKeepsTheBak()
    {
        SocialListStore.Save(ListPath, Mine());
        var oldText = File.ReadAllText(ListPath);

        var result = ConfigImporter.Import(ListPath, Theirs(), ImportMode.Merge, _clock);

        Assert.True(result.Success, result.Error);
        var now = SocialListStore.Load(ListPath);
        Assert.Equal("Mine", now.Config.Name);
        Assert.Equal(new[] { "Nitebird", "Zed_Zed" }, now.Config.Players.Select(p => p.Handle));
        Assert.Equal(OverlayColor.Cyan, now.Config.Players[0].Color);
        Assert.Single(now.Config.Orgs);
        Assert.Single(now.Config.OrgRules);
        Assert.Equal(1, result.PlayersAdded);
        Assert.Equal(1, result.RulesAdded);
        Assert.Equal(1, result.DuplicatesSkipped);
        Assert.Equal(oldText, File.ReadAllText(ListPath + ".bak"));
        Assert.Null(result.BackupPath);
        Assert.Equal(2, result.Config!.Players.Count);
    }

    [Fact]
    public void MergeMode_RefusesWhenTheCurrentFileIsInvalid_AndChangesNothing()
    {
        File.WriteAllText(ListPath, "not toml at all ===");

        var result = ConfigImporter.Import(ListPath, Theirs(), ImportMode.Merge, _clock);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("not toml at all ===", File.ReadAllText(ListPath));
    }

    [Fact]
    public void ReplaceMode_OverwritesTheListFile()
    {
        SocialListStore.Save(ListPath, Mine());

        var result = ConfigImporter.Import(ListPath, Theirs(), ImportMode.Replace, _clock);

        Assert.True(result.Success, result.Error);
        var now = SocialListStore.Load(ListPath);
        Assert.Equal("Theirs", now.Config.Name);
        Assert.Empty(now.Config.Orgs);
        Assert.Equal(2, now.Config.Players.Count);
        Assert.Equal(2, result.PlayersAdded);
        Assert.Contains("replaced", result.Summary);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.backup-*"));
    }

    [Theory]
    [InlineData(ImportMode.BackupExisting)]
    [InlineData(ImportMode.Merge)]
    [InlineData(ImportMode.Replace)]
    public void NoExistingFile_JustWritesTheImportedConfig(ImportMode mode)
    {
        var result = ConfigImporter.Import(ListPath, Theirs(), mode, _clock);

        Assert.True(result.Success, result.Error);
        Assert.False(result.HadExistingFile);
        var now = SocialListStore.Load(ListPath);
        Assert.Equal("Theirs", now.Config.Name);
        Assert.Equal(2, now.Config.Players.Count);
        Assert.Null(result.BackupPath);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.backup-*"));
        Assert.Equal("Imported: 2 players, 0 orgs, 1 rule", result.Summary);
    }

    [Fact]
    public void Summary_ForMerge_NamesTheAddedCountsAndSkippedDuplicates()
    {
        SocialListStore.Save(ListPath, Mine());

        var result = ConfigImporter.Import(ListPath, Theirs(), ImportMode.Merge, _clock);

        Assert.Equal("Imported: 1 player, 0 orgs, 1 rule added, 1 duplicate skipped", result.Summary);
    }

    [Fact]
    public void ReadFile_ReadsTextAndDropsTheByteOrderMark()
    {
        var path = _dir.File("in.toml");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("name = \"x\"")).ToArray());

        var read = ConfigImporter.ReadFile(path);

        Assert.True(read.Success);
        Assert.Equal("name = \"x\"", read.Text);
    }

    [Fact]
    public void ReadFile_RefusesFilesOverOneMegabyte_AndMissingFiles()
    {
        var big = _dir.File("big.toml");
        File.WriteAllBytes(big, new byte[ConfigImporter.MaxFileBytes + 1]);
        var exactly = _dir.File("exact.toml");
        File.WriteAllBytes(exactly, new byte[ConfigImporter.MaxFileBytes]);

        var tooBig = ConfigImporter.ReadFile(big);

        Assert.False(tooBig.Success);
        Assert.Contains("too large", tooBig.Error);
        Assert.True(ConfigImporter.ReadFile(exactly).Success);
        Assert.False(ConfigImporter.ReadFile(_dir.File("nope.toml")).Success);
    }

    [Fact]
    public void Export_WritesAFileThatReadsBack_AndLeavesNoTempOrBak()
    {
        var path = _dir.File("astra-config.toml");

        var saved = ConfigImporter.Export(path, Mine());

        Assert.True(saved.Success, saved.Error);
        var read = SocialConfigToml.Read(File.ReadAllText(path));
        Assert.True(read.IsValid);
        Assert.Equal("Mine", read.Config!.Name);
        Assert.False(File.Exists(path + ".tmp"));

        ConfigImporter.Export(path, Theirs()); // overwrite
        Assert.False(File.Exists(path + ".bak"));
        Assert.Equal("Theirs", SocialConfigToml.Read(File.ReadAllText(path)).Config!.Name);
    }
}
