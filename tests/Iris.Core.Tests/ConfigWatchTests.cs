using System.Text;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>Shared helpers for the tests of watched config files, last-good copies, startup and polling.</summary>
internal sealed class WatchEnv : IDisposable
{
    public static readonly DateTime T1 = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    public TempDir Dir { get; } = new();
    public FakeClock Clock { get; } = new();
    public ListLog Log { get; } = new();
    public DataPaths Paths { get; }

    public WatchEnv()
    {
        Paths = DataPaths.FromBaseDirectory(Dir.Path);
        Paths.EnsureCreated();
    }

    public WatchedFile ListFile(FileChangeDetector? detector = null) =>
        new(Paths.SocialListFile, Paths.SocialListLastGoodFile, Clock, Log, detector);

    public WatchedFile SettingsFile(FileChangeDetector? detector = null) =>
        new(Paths.SettingsFile, Paths.SettingsLastGoodFile, Clock, Log, detector);

    public static string ListText(params string[] players)
    {
        var config = new SocialConfig { Name = "Test list" };
        foreach (var p in players)
            config.Players.Add(new PlayerEntry(p, OverlayColor.Cyan));
        return SocialConfigToml.Write(config);
    }

    /// <summary>Valid TOML, wrong content: one good player, one handle that is too short (an error).</summary>
    public const string ListWithOneBadRow =
        "format = \"sc-social-overlay\"\nformat_version = 1\n\n[[players]]\nhandle = \"Nitebird\"\ncolor = \"Cyan\"\n\n[[players]]\nhandle = \"ab\"\ncolor = \"Cyan\"\n";

    public const string NotToml = "this is [not toml\n";

    public static string SettingsText(int ttl = 30) =>
        AppSettingsToml.Write(new AppSettings { CacheTtlDays = ttl });

    /// <summary>Writes text the way an editor would and sets a modification time (a later one by default, so the change is seen).</summary>
    public static void Edit(string path, string text, int minutesAfterT1 = 1)
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, T1.AddMinutes(minutesAfterT1));
    }

    public string Read(string path) => File.ReadAllText(path);

    public void Dispose() => Dir.Dispose();
}

public class DataPathsLastGoodTests
{
    [Fact]
    public void LastGoodFiles_LiveInTheDataFolder()
    {
        using var dir = new TempDir();
        var paths = DataPaths.FromBaseDirectory(dir.Path);

        Assert.Equal(Path.Combine(dir.Path, "Data", "social-overlay.lastgood.toml"), paths.SocialListLastGoodFile);
        Assert.Equal(Path.Combine(dir.Path, "Data", "settings.lastgood.toml"), paths.SettingsLastGoodFile);
    }

    [Fact]
    public void IsOwnConfigFile_RecognisesTheListAndSettingsFiles_ByFullPath_IgnoringCase()
    {
        using var dir = new TempDir();
        var paths = DataPaths.FromBaseDirectory(dir.Path);

        Assert.True(paths.IsOwnConfigFile(paths.SocialListFile));
        Assert.True(paths.IsOwnConfigFile(paths.SettingsFile));
        Assert.True(paths.IsOwnConfigFile(paths.SocialListFile.ToUpperInvariant()));
        Assert.True(paths.IsOwnConfigFile(Path.Combine(dir.Path, "Data", "..", "Data", "settings.toml")));
        Assert.False(paths.IsOwnConfigFile(Path.Combine(dir.Path, "iris-config.toml")));
        Assert.False(paths.IsOwnConfigFile(paths.SocialListLastGoodFile));
        Assert.False(paths.IsOwnConfigFile(string.Empty));
    }
}

public class LastGoodWritingTests
{
    [Fact]
    public void WriteLastGood_StoresTheExactText_AndLeavesNoTempFile()
    {
        using var env = new WatchEnv();
        var file = env.ListFile();
        const string text = "# my own comment\nformat = \"sc-social-overlay\"\r\nformat_version = 1\n\n\n";

        file.WriteLastGood(text);

        Assert.Equal(text, File.ReadAllText(env.Paths.SocialListLastGoodFile));
        Assert.False(File.Exists(env.Paths.SocialListLastGoodFile + ".tmp"));
        Assert.False(File.Exists(env.Paths.SocialListLastGoodFile + ".bak"));
        Assert.False(File.Exists(env.Paths.SocialListFile));   // the main file is never created or touched by this
    }

    [Fact]
    public void WriteLastGood_ReplacesAnOlderCopy()
    {
        using var env = new WatchEnv();
        var file = env.ListFile();

        file.WriteLastGood("one\n");
        file.WriteLastGood("two\n");

        Assert.Equal("two\n", File.ReadAllText(env.Paths.SocialListLastGoodFile));
    }

    [Fact]
    public void ReadLastGood_ReturnsTextTimeAndHash_OrNull()
    {
        using var env = new WatchEnv();
        var file = env.ListFile();
        Assert.Null(file.ReadLastGood());

        file.WriteLastGood("text\n");
        File.SetLastWriteTimeUtc(env.Paths.SocialListLastGoodFile, WatchEnv.T1);
        var copy = file.ReadLastGood();

        Assert.NotNull(copy);
        Assert.Equal("text\n", copy!.Text);
        Assert.Equal(new DateTimeOffset(WatchEnv.T1, TimeSpan.Zero), copy.WrittenUtc);
        Assert.Equal(FileChangeDetector.HashOf("text\n"), copy.Hash);
    }

    [Fact]
    public void Saving_TheList_WritesItsLastGoodCopy_AndIsNotAnOutsideEdit()
    {
        using var env = new WatchEnv();
        var file = env.ListFile();
        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Red));

        var result = SocialListStore.Save(env.Paths.SocialListFile, config, file);

        Assert.True(result.Success);
        Assert.Equal(env.Read(env.Paths.SocialListFile), env.Read(env.Paths.SocialListLastGoodFile));
        Assert.Equal(FileChangeKind.Unchanged, file.Detector.Check().Kind);
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
    }

    [Fact]
    public void Saving_TheSettings_WritesTheirLastGoodCopy_AndIsNotAnOutsideEdit()
    {
        using var env = new WatchEnv();
        var file = env.SettingsFile();

        var result = AppSettingsToml.Save(env.Paths.SettingsFile, new AppSettings { CacheTtlDays = 7 }, file);

        Assert.True(result.Success);
        Assert.Equal(env.Read(env.Paths.SettingsFile), env.Read(env.Paths.SettingsLastGoodFile));
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSettings(file).Kind);
    }

    [Fact]
    public void ASaveThatIsRefused_LeavesTheLastGoodCopyAlone()
    {
        using var env = new WatchEnv();
        var file = env.ListFile();
        file.WriteLastGood("old good\n");
        var broken = new SocialConfig();
        broken.Players.Add(new PlayerEntry("ab", OverlayColor.Red));   // too short: Save refuses

        var result = SocialListStore.Save(env.Paths.SocialListFile, broken, file);

        Assert.False(result.Success);
        Assert.Equal("old good\n", env.Read(env.Paths.SocialListLastGoodFile));
        Assert.False(File.Exists(env.Paths.SocialListFile));
    }

    [Fact]
    public void Importing_WritesTheLastGoodCopy_InEveryMode()
    {
        foreach (var mode in new[] { ImportMode.BackupExisting, ImportMode.Merge, ImportMode.Replace })
        {
            using var env = new WatchEnv();
            var file = env.ListFile();
            var mine = new SocialConfig();
            mine.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Red));
            SocialListStore.Save(env.Paths.SocialListFile, mine, file);
            var imported = new SocialConfig();
            imported.Players.Add(new PlayerEntry("Otherone", OverlayColor.Blue));

            var result = ConfigImporter.Import(env.Paths.SocialListFile, imported, mode, env.Clock, file);

            Assert.True(result.Success, mode.ToString());
            Assert.Equal(env.Read(env.Paths.SocialListFile), env.Read(env.Paths.SocialListLastGoodFile));
            Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
        }
    }
}

public class StartupFallbackTests
{
    [Fact]
    public void ValidList_IsUsed_AndItsExactTextBecomesTheLastGoodCopy()
    {
        using var env = new WatchEnv();
        var text = "# written by hand\n" + WatchEnv.ListText("Nitebird") + "\n\n# trailing comment\n";
        File.WriteAllText(env.Paths.SocialListFile, text);
        var file = env.ListFile();

        var startup = ConfigStartup.LoadSocialList(file);

        Assert.True(startup.Load.IsValid);
        Assert.False(startup.UsedLastGood);
        Assert.False(startup.MainFileHasErrors);
        Assert.Equal("Nitebird", Assert.Single(startup.Load.Config.Players).Handle);
        Assert.Equal(text, env.Read(env.Paths.SocialListLastGoodFile));
        Assert.Equal(FileChangeKind.Unchanged, file.Detector.Check().Kind);   // the startup read counts as "loaded"
    }

    [Fact]
    public void ListWithOnlyWarnings_IsValid_AndGetsALastGoodCopy()
    {
        using var env = new WatchEnv();
        var text = WatchEnv.ListText("Nitebird") + "future_key = 1\n";
        File.WriteAllText(env.Paths.SocialListFile, text);

        var startup = ConfigStartup.LoadSocialList(env.ListFile());

        Assert.True(startup.Load.IsValid);
        Assert.NotEmpty(startup.Load.Warnings);
        Assert.Equal(text, env.Read(env.Paths.SocialListLastGoodFile));
    }

    [Fact]
    public void InvalidList_WithALastGoodCopy_UsesTheCopy_AndDoesNotTouchTheMainFile()
    {
        using var env = new WatchEnv();
        var good = WatchEnv.ListText("GoodOne");
        File.WriteAllText(env.Paths.SocialListLastGoodFile, good);
        File.SetLastWriteTimeUtc(env.Paths.SocialListLastGoodFile, WatchEnv.T1);
        File.WriteAllText(env.Paths.SocialListFile, WatchEnv.ListWithOneBadRow);
        var mainBytes = File.ReadAllBytes(env.Paths.SocialListFile);
        var mainTime = File.GetLastWriteTimeUtc(env.Paths.SocialListFile);

        var startup = ConfigStartup.LoadSocialList(env.ListFile());

        Assert.True(startup.UsedLastGood);
        Assert.True(startup.Load.IsValid);
        Assert.Equal("GoodOne", Assert.Single(startup.Load.Config.Players).Handle);
        Assert.Equal(new DateTimeOffset(WatchEnv.T1, TimeSpan.Zero), startup.LastGoodTime);
        Assert.True(startup.MainFileHasErrors);
        Assert.Contains(startup.MainFileErrors, e => e.Contains("ab"));
        Assert.NotNull(startup.MainFileHash);
        // the main file is exactly as the user left it, the last-good copy is exactly as it was
        Assert.Equal(mainBytes, File.ReadAllBytes(env.Paths.SocialListFile));
        Assert.Equal(mainTime, File.GetLastWriteTimeUtc(env.Paths.SocialListFile));
        Assert.Equal(good, env.Read(env.Paths.SocialListLastGoodFile));
        Assert.Equal(WatchEnv.ListWithOneBadRow, env.Read(env.Paths.SocialListFile));
    }

    [Fact]
    public void ListThatIsNotEvenToml_WithALastGoodCopy_UsesTheCopy()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SocialListLastGoodFile, WatchEnv.ListText("GoodOne"));
        File.WriteAllText(env.Paths.SocialListFile, WatchEnv.NotToml);

        var startup = ConfigStartup.LoadSocialList(env.ListFile());

        Assert.True(startup.UsedLastGood);
        Assert.Contains(startup.MainFileErrors, e => e.StartsWith("TOML syntax"));
    }

    [Fact]
    public void InvalidList_WithoutALastGoodCopy_KeepsTheOldBehaviour_ValidRowsLoadAndProblemsShow()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SocialListFile, WatchEnv.ListWithOneBadRow);

        var startup = ConfigStartup.LoadSocialList(env.ListFile());

        Assert.False(startup.UsedLastGood);
        Assert.False(startup.Load.IsValid);
        Assert.Equal("Nitebird", Assert.Single(startup.Load.Config.Players).Handle);
        Assert.NotEmpty(startup.Load.Errors);
        Assert.False(File.Exists(env.Paths.SocialListLastGoodFile));   // a broken file never becomes the last good one
    }

    [Fact]
    public void InvalidList_WithAnInvalidLastGoodCopy_DoesNotUseIt()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SocialListLastGoodFile, WatchEnv.NotToml);
        File.WriteAllText(env.Paths.SocialListFile, WatchEnv.ListWithOneBadRow);

        var startup = ConfigStartup.LoadSocialList(env.ListFile());

        Assert.False(startup.UsedLastGood);
        Assert.Equal("Nitebird", Assert.Single(startup.Load.Config.Players).Handle);
    }

    [Fact]
    public void MissingList_IsAFreshStart_AndCreatesNothing()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SocialListLastGoodFile, WatchEnv.ListText("GoodOne"));   // even with a leftover copy

        var startup = ConfigStartup.LoadSocialList(env.ListFile());

        Assert.True(startup.Load.IsNew);
        Assert.False(startup.UsedLastGood);
        Assert.Empty(startup.Load.Config.Players);
        Assert.False(File.Exists(env.Paths.SocialListFile));
    }

    [Fact]
    public void ListThatIsLockedAtStartup_UsesTheLastGoodCopy_AndIsReadAsSoonAsItIsFree()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SocialListLastGoodFile, WatchEnv.ListText("GoodOne"));
        File.WriteAllText(env.Paths.SocialListFile, WatchEnv.ListText("NewOne"));
        var locked = true;
        var detector = new FileChangeDetector(env.Paths.SocialListFile, _ => { }, p => locked ? throw new IOException("in use") : File.ReadAllBytes(p));
        var file = env.ListFile(detector);

        var startup = ConfigStartup.LoadSocialList(file);

        Assert.True(startup.UsedLastGood);
        Assert.Equal(ConfigStartup.FileInUseMessage, Assert.Single(startup.MainFileErrors));

        locked = false;
        var poll = ConfigPoller.PollSocialList(file);
        Assert.Equal(PollKind.Applied, poll.Kind);
        Assert.Equal("NewOne", Assert.Single(poll.Config!.Players).Handle);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void ValidSettings_AreUsed_AndGetALastGoodCopy()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SettingsFile, WatchEnv.SettingsText(7));

        var startup = ConfigStartup.LoadSettings(env.SettingsFile());

        Assert.Equal(7, startup.Load.Settings.CacheTtlDays);
        Assert.False(startup.UsedLastGood);
        Assert.Equal(WatchEnv.SettingsText(7), env.Read(env.Paths.SettingsLastGoodFile));
    }

    [Fact]
    public void SettingsWithABadSingleValue_AreStillValid_UseTheDefaultForIt_AndGetALastGoodCopy()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SettingsFile, "format_version = 1\ncache_ttl_days = 5\n");

        var startup = ConfigStartup.LoadSettings(env.SettingsFile());

        Assert.True(startup.Load.IsValid);
        Assert.Equal(30, startup.Load.Settings.CacheTtlDays);
        Assert.NotEmpty(startup.Load.Warnings);
        Assert.True(File.Exists(env.Paths.SettingsLastGoodFile));
    }

    [Theory]
    [InlineData("not = [valid toml\n")]
    [InlineData("format_version = 2\ncache_ttl_days = 7\n")]
    public void BrokenSettings_WithALastGoodCopy_UseTheCopy_AndLeaveTheMainFileAlone(string broken)
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SettingsLastGoodFile, WatchEnv.SettingsText(7));
        File.WriteAllText(env.Paths.SettingsFile, broken);

        var startup = ConfigStartup.LoadSettings(env.SettingsFile());

        Assert.True(startup.UsedLastGood);
        Assert.Equal(7, startup.Load.Settings.CacheTtlDays);
        Assert.True(startup.MainFileHasErrors);
        Assert.Equal(broken, env.Read(env.Paths.SettingsFile));
        Assert.Equal(WatchEnv.SettingsText(7), env.Read(env.Paths.SettingsLastGoodFile));
    }

    [Fact]
    public void BrokenSettings_WithoutALastGoodCopy_FallBackToTheDefaults()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.SettingsFile, "not = [valid toml\n");

        var startup = ConfigStartup.LoadSettings(env.SettingsFile());

        Assert.False(startup.UsedLastGood);
        Assert.False(startup.Load.IsValid);
        Assert.Equal(new AppSettings(), startup.Load.Settings);
        Assert.False(File.Exists(env.Paths.SettingsLastGoodFile));
    }

    [Fact]
    public void MissingSettings_AreTheDefaults_AndNothingIsCreated()
    {
        using var env = new WatchEnv();

        var startup = ConfigStartup.LoadSettings(env.SettingsFile());

        Assert.True(startup.Load.IsNew);
        Assert.Equal(new AppSettings(), startup.Load.Settings);
        Assert.False(File.Exists(env.Paths.SettingsFile));
        Assert.False(File.Exists(env.Paths.SettingsLastGoodFile));
    }

    [Fact]
    public void Messages_MatchTheWordingOfTheSpec()
    {
        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Red));
        config.Players.Add(new PlayerEntry("Otherone", OverlayColor.Red));
        config.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));

        Assert.Equal("New config loaded: 2 players, 1 org, 0 rules", ConfigMessages.NewConfigLoaded(config));
        Assert.Equal("Exclusion list reloaded (61 names)", ConfigMessages.ExclusionsReloaded(61));
        Assert.Equal(
            "social-overlay.toml has errors — still using the previous version. Open the config window for details.",
            ConfigMessages.StillUsingPrevious("social-overlay.toml"));
        Assert.StartsWith("settings.toml has errors — using the last good version (from ", ConfigMessages.UsingLastGood("settings.toml", DateTimeOffset.UtcNow));
        Assert.Matches(@"\(from \d{4}-\d\d-\d\d \d\d:\d\d\)$", ConfigMessages.UsingLastGood("settings.toml", DateTimeOffset.UtcNow));
        Assert.Equal("Your hosted config has not changed.", ConfigMessages.HostedConfigUnchanged);
    }
}

public class ConfigPollerTests
{
    private static ConfigPoll Poll(WatchEnv env, WatchedFile list, WatchedFile settings, ExclusionList exclusions) =>
        ConfigPoller.PollAll(list, settings, exclusions);

    private static WatchedFile LoadedList(WatchEnv env, string text)
    {
        File.WriteAllText(env.Paths.SocialListFile, text);
        File.SetLastWriteTimeUtc(env.Paths.SocialListFile, WatchEnv.T1);
        var file = env.ListFile();
        ConfigStartup.LoadSocialList(file);
        return file;
    }

    [Fact]
    public void NothingChanged_IsNothing()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));

        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
    }

    [Fact]
    public void AValidEdit_IsApplied_AndBecomesTheLastGoodCopy()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));
        var edited = WatchEnv.ListText("Nitebird", "Otherone");

        WatchEnv.Edit(env.Paths.SocialListFile, edited);
        var poll = ConfigPoller.PollSocialList(file);

        Assert.Equal(PollKind.Applied, poll.Kind);
        Assert.Equal(2, poll.Config!.Players.Count);
        Assert.Equal(edited, env.Read(env.Paths.SocialListLastGoodFile));
        Assert.Equal(poll.Hash, file.AppliedHash);
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
    }

    [Fact]
    public void AnEditWithOnlyWarnings_IsApplied_WithTheWarnings()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));

        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.ListText("Nitebird") + "future_key = 1\n");
        var poll = ConfigPoller.PollSocialList(file);

        Assert.Equal(PollKind.Applied, poll.Kind);
        Assert.NotEmpty(poll.Warnings);
    }

    [Fact]
    public void AnInvalidEdit_IsRejected_ReportedOnce_AndTheLastGoodCopyStaysAsItWas()
    {
        using var env = new WatchEnv();
        var good = WatchEnv.ListText("Nitebird");
        var file = LoadedList(env, good);

        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.ListWithOneBadRow);
        var first = ConfigPoller.PollSocialList(file);
        var second = ConfigPoller.PollSocialList(file);

        Assert.Equal(PollKind.Rejected, first.Kind);
        Assert.NotEmpty(first.Errors);
        Assert.NotNull(first.Hash);
        Assert.Equal(PollKind.Nothing, second.Kind);   // each bad version only once
        Assert.Equal(good, env.Read(env.Paths.SocialListLastGoodFile));
        Assert.Equal(FileChangeDetector.HashOf(good), file.AppliedHash);   // still using the old one
    }

    [Fact]
    public void ASyntaxErrorInTheEdit_IsRejected_WithTheParsersMessage()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));

        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.NotToml);
        var poll = ConfigPoller.PollSocialList(file);

        Assert.Equal(PollKind.Rejected, poll.Kind);
        Assert.StartsWith("TOML syntax", poll.Errors[0]);
    }

    [Fact]
    public void FixingABadEdit_AppliesTheNewVersion()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));
        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.ListWithOneBadRow);
        ConfigPoller.PollSocialList(file);

        var fixedText = WatchEnv.ListText("Nitebird", "Otherone");
        WatchEnv.Edit(env.Paths.SocialListFile, fixedText, minutesAfterT1: 2);
        var poll = ConfigPoller.PollSocialList(file);

        Assert.Equal(PollKind.Applied, poll.Kind);
        Assert.Equal(fixedText, env.Read(env.Paths.SocialListLastGoodFile));
    }

    [Fact]
    public void ChangingBackToTheVersionInUse_IsRestored_NotNew()
    {
        using var env = new WatchEnv();
        var good = WatchEnv.ListText("Nitebird");
        var file = LoadedList(env, good);
        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.NotToml);
        ConfigPoller.PollSocialList(file);

        WatchEnv.Edit(env.Paths.SocialListFile, good, minutesAfterT1: 2);
        var poll = ConfigPoller.PollSocialList(file);

        Assert.Equal(PollKind.Restored, poll.Kind);
        Assert.Null(poll.Config);
    }

    [Fact]
    public void AMissingList_IsReportedOnce()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));
        File.Delete(env.Paths.SocialListFile);

        Assert.Equal(PollKind.Missing, ConfigPoller.PollSocialList(file).Kind);
        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
        Assert.Equal(WatchEnv.ListText("Nitebird"), env.Read(env.Paths.SocialListLastGoodFile));   // the copy survives a deleted file
    }

    [Fact]
    public void AListThatAppearsAfterStartupWithoutOne_IsApplied()
    {
        using var env = new WatchEnv();
        var file = env.ListFile();
        ConfigStartup.LoadSocialList(file);   // no file: fresh start

        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.ListText("Nitebird"));

        Assert.Equal(PollKind.Applied, ConfigPoller.PollSocialList(file).Kind);
    }

    [Fact]
    public void OurOwnSave_IsNeverApplied_EvenIfACheckStraddlesIt()
    {
        using var env = new WatchEnv();
        var file = LoadedList(env, WatchEnv.ListText("Nitebird"));
        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry("Otherone", OverlayColor.Red));

        SocialListStore.Save(env.Paths.SocialListFile, config, file);

        Assert.Equal(PollKind.Nothing, ConfigPoller.PollSocialList(file).Kind);
    }

    // ------------------------------------------------------------------ settings

    private static WatchedFile LoadedSettings(WatchEnv env, string text)
    {
        File.WriteAllText(env.Paths.SettingsFile, text);
        File.SetLastWriteTimeUtc(env.Paths.SettingsFile, WatchEnv.T1);
        var file = env.SettingsFile();
        ConfigStartup.LoadSettings(file);
        return file;
    }

    [Fact]
    public void ValidSettingsEdit_IsApplied_AndBecomesTheLastGoodCopy()
    {
        using var env = new WatchEnv();
        var file = LoadedSettings(env, WatchEnv.SettingsText(30));

        WatchEnv.Edit(env.Paths.SettingsFile, WatchEnv.SettingsText(7));
        var poll = ConfigPoller.PollSettings(file);

        Assert.Equal(PollKind.Applied, poll.Kind);
        Assert.Equal(7, poll.Settings!.CacheTtlDays);
        Assert.Equal(WatchEnv.SettingsText(7), env.Read(env.Paths.SettingsLastGoodFile));
    }

    [Fact]
    public void ABadSingleSettingsValue_IsStillApplied_WithAWarning()
    {
        using var env = new WatchEnv();
        var file = LoadedSettings(env, WatchEnv.SettingsText(7));

        WatchEnv.Edit(env.Paths.SettingsFile, "format_version = 1\ncache_ttl_days = 5\n");
        var poll = ConfigPoller.PollSettings(file);

        Assert.Equal(PollKind.Applied, poll.Kind);
        Assert.Equal(30, poll.Settings!.CacheTtlDays);
        Assert.NotEmpty(poll.Warnings);
    }

    [Theory]
    [InlineData("not = [valid toml\n")]
    [InlineData("format_version = 99\n")]
    public void BrokenSettingsEdit_IsRejected_ReportedOnce_AndTheLastGoodCopyIsKept(string broken)
    {
        using var env = new WatchEnv();
        var file = LoadedSettings(env, WatchEnv.SettingsText(7));

        WatchEnv.Edit(env.Paths.SettingsFile, broken);
        var first = ConfigPoller.PollSettings(file);
        var second = ConfigPoller.PollSettings(file);

        Assert.Equal(PollKind.Rejected, first.Kind);
        Assert.NotEmpty(first.Errors);
        Assert.Equal(PollKind.Nothing, second.Kind);
        Assert.Equal(WatchEnv.SettingsText(7), env.Read(env.Paths.SettingsLastGoodFile));
    }

    // ------------------------------------------------------------------ the exclusion list, polled together

    [Fact]
    public void PollAll_ReportsAnEditedExclusionList_AndCountsTheNames()
    {
        using var env = new WatchEnv();
        var list = LoadedList(env, WatchEnv.ListText("Nitebird"));
        var settings = LoadedSettings(env, WatchEnv.SettingsText());
        File.WriteAllText(env.Paths.ExclusionsFile, "Gold\n");
        var exclusions = ExclusionList.LoadOrCreate(env.Paths.ExclusionsFile, env.Clock, env.Log);

        var quiet = Poll(env, list, settings, exclusions);
        Assert.True(quiet.IsEmpty);

        WatchEnv.Edit(env.Paths.ExclusionsFile, "Gold\nCopper\nZinc\n");
        var poll = Poll(env, list, settings, exclusions);

        Assert.True(poll.ExclusionsReloaded);
        Assert.Equal(3, poll.ExclusionCount);
        Assert.False(poll.IsEmpty);
        Assert.True(exclusions.IsExcluded("copper"));
        Assert.True(Poll(env, list, settings, exclusions).IsEmpty);
    }

    [Fact]
    public void ExclusionList_TouchedWithoutChangingTheNames_IsNotReported()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.ExclusionsFile, "Gold\n");
        var exclusions = ExclusionList.LoadOrCreate(env.Paths.ExclusionsFile, env.Clock, env.Log);

        File.SetLastWriteTimeUtc(env.Paths.ExclusionsFile, WatchEnv.T1);

        Assert.False(exclusions.ReloadIfChanged());
    }

    [Fact]
    public void ExclusionList_HandEdit_IsReportedByReloadIfChanged()
    {
        using var env = new WatchEnv();
        File.WriteAllText(env.Paths.ExclusionsFile, "Gold\n");
        var exclusions = ExclusionList.LoadOrCreate(env.Paths.ExclusionsFile, env.Clock, env.Log);

        WatchEnv.Edit(env.Paths.ExclusionsFile, "Gold\nCopper\n");

        Assert.True(exclusions.ReloadIfChanged());
        Assert.False(exclusions.ReloadIfChanged());
        Assert.Equal(2, exclusions.Count);
    }

    // ------------------------------------------------------------------ results that are out of date by the time they are applied

    [Fact]
    public void DropOutdated_KeepsAFreshResult_AndReplacesOneThatOurOwnSaveOvertook()
    {
        using var env = new WatchEnv();
        var list = LoadedList(env, WatchEnv.ListText("Nitebird"));
        var settings = LoadedSettings(env, WatchEnv.SettingsText());
        var exclusions = ExclusionList.LoadOrCreate(env.Paths.ExclusionsFile, env.Clock, env.Log);

        WatchEnv.Edit(env.Paths.SocialListFile, WatchEnv.ListText("Nitebird", "Otherone"));
        var poll = Poll(env, list, settings, exclusions);
        Assert.Equal(PollKind.Applied, poll.List.Kind);
        Assert.Same(poll, ConfigPoller.DropOutdated(poll, list, settings));   // nothing happened since

        var config = new SocialConfig();
        config.Players.Add(new PlayerEntry("Thirdone", OverlayColor.Red));
        SocialListStore.Save(env.Paths.SocialListFile, config, list);          // the window saved while the result was parked

        var dropped = ConfigPoller.DropOutdated(poll, list, settings);
        Assert.Equal(PollKind.Nothing, dropped.List.Kind);
        Assert.Equal(poll.Settings.Kind, dropped.Settings.Kind);
        Assert.Equal(poll.ExclusionsReloaded, dropped.ExclusionsReloaded);
    }

    [Fact]
    public void DropOutdated_AlsoDropsAnOutdatedRejectedSettingsResult()
    {
        using var env = new WatchEnv();
        var list = LoadedList(env, WatchEnv.ListText("Nitebird"));
        var settings = LoadedSettings(env, WatchEnv.SettingsText());
        var exclusions = ExclusionList.LoadOrCreate(env.Paths.ExclusionsFile, env.Clock, env.Log);

        WatchEnv.Edit(env.Paths.SettingsFile, WatchEnv.NotToml);
        var poll = Poll(env, list, settings, exclusions);
        Assert.Equal(PollKind.Rejected, poll.Settings.Kind);

        AppSettingsToml.Save(env.Paths.SettingsFile, new AppSettings { CacheTtlDays = 7 }, settings);   // the user fixed it in the window

        Assert.Equal(PollKind.Nothing, ConfigPoller.DropOutdated(poll, list, settings).Settings.Kind);
    }

    [Fact]
    public void DropOutdated_LeavesAMissingResultAlone()
    {
        using var env = new WatchEnv();
        var list = LoadedList(env, WatchEnv.ListText("Nitebird"));
        var settings = LoadedSettings(env, WatchEnv.SettingsText());
        var exclusions = ExclusionList.LoadOrCreate(env.Paths.ExclusionsFile, env.Clock, env.Log);
        File.Delete(env.Paths.SocialListFile);

        var poll = Poll(env, list, settings, exclusions);

        Assert.Equal(PollKind.Missing, poll.List.Kind);
        Assert.Same(poll, ConfigPoller.DropOutdated(poll, list, settings));
    }
}
