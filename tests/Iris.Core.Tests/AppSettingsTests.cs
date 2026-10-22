using Iris.Core.Cache;
using Iris.Core.Config;
using Iris.Core.Logging;
using Iris.Core.Settings;
using Xunit;

namespace Iris.Core.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Defaults_AreAsSpecified()
    {
        var s = new AppSettings();

        Assert.Equal(30, s.CacheTtlDays);
        Assert.True(s.SessionLogEnabled);
        Assert.True(s.CheckForUpdates);
        Assert.Equal(string.Empty, s.HostedConfigUrl);
        Assert.Equal(string.Empty, s.HostedConfigLastHash);
        Assert.False(s.HostedConfigAutoCheck);
        Assert.Null(s.HostedConfigLastCheck);
    }

    [Fact]
    public void Load_MissingFile_GivesDefaultsAndWritesNothing()
    {
        var path = _dir.File("settings.toml");

        var loaded = AppSettingsToml.Load(path);

        Assert.True(loaded.IsNew);
        Assert.True(loaded.IsValid);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(new AppSettings(), loaded.Settings);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = _dir.File("Data" + Path.DirectorySeparatorChar + "settings.toml");
        var settings = new AppSettings
        {
            CacheTtlDays = 7,
            SessionLogEnabled = false,
            CheckForUpdates = false,
            HostedConfigUrl = "https://example.com/cfg/iris-config.toml?token=a&b=c",
            HostedConfigLastHash = Hash,
        };

        var saved = AppSettingsToml.Save(path, settings);
        var loaded = AppSettingsToml.Load(path);

        Assert.True(saved.Success, saved.Error);
        Assert.False(loaded.IsNew);
        Assert.True(loaded.IsValid);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(settings, loaded.Settings);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SecondSave_KeepsTheFirstAsBackup()
    {
        var path = _dir.File("settings.toml");
        AppSettingsToml.Save(path, new AppSettings());
        var first = File.ReadAllText(path);

        AppSettingsToml.Save(path, new AppSettings { CacheTtlDays = 7 });

        Assert.Equal(first, File.ReadAllText(path + ".bak"));
        Assert.Equal(7, AppSettingsToml.Load(path).Settings.CacheTtlDays);
    }

    [Theory]
    [InlineData("cache_ttl_days = 14")]
    [InlineData("cache_ttl_days = 0")]
    [InlineData("cache_ttl_days = \"7\"")]
    public void InvalidTtl_BecomesThirtyWithAWarning(string line)
    {
        var result = AppSettingsToml.Read("format_version = 1\n" + line + "\nsession_log_enabled = false\n");

        Assert.True(result.IsValid);
        Assert.Equal(30, result.Settings.CacheTtlDays);
        Assert.Contains(result.Warnings, w => w.StartsWith("cache_ttl_days"));
        Assert.False(result.Settings.SessionLogEnabled); // the other settings are still read
    }

    [Fact]
    public void ValidTtl_SevenAndThirtyAreAccepted()
    {
        Assert.Equal(7, AppSettingsToml.Read("cache_ttl_days = 7").Settings.CacheTtlDays);
        Assert.Equal(30, AppSettingsToml.Read("cache_ttl_days = 30").Settings.CacheTtlDays);
    }

    [Fact]
    public void UnknownKey_IsAWarningNotAnError()
    {
        var result = AppSettingsToml.Read("format_version = 1\nfuture_setting = 5\ncache_ttl_days = 7\n");

        Assert.True(result.IsValid);
        Assert.Single(result.Warnings);
        Assert.Contains("future_setting", result.Warnings[0]);
        Assert.Equal(7, result.Settings.CacheTtlDays);
    }

    [Fact]
    public void BadToml_IsAnErrorWithDefaults()
    {
        var result = AppSettingsToml.Read("cache_ttl_days = = 7\n[[[");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(new AppSettings(), result.Settings);
    }

    [Fact]
    public void BadTomlFile_Load_IsAnErrorWithDefaults_AndTheFileIsUntouched()
    {
        var path = _dir.File("settings.toml");
        File.WriteAllText(path, "this is not toml ===");

        var loaded = AppSettingsToml.Load(path);

        Assert.False(loaded.IsValid);
        Assert.Equal(new AppSettings(), loaded.Settings);
        Assert.Equal("this is not toml ===", File.ReadAllText(path));
    }

    [Fact]
    public void NewerFormatVersion_IsAnErrorWithDefaults()
    {
        var result = AppSettingsToml.Read("format_version = 2\ncache_ttl_days = 7\n");

        Assert.False(result.IsValid);
        Assert.Equal(30, result.Settings.CacheTtlDays);
    }

    [Fact]
    public void WrongTypedValues_WarnAndFallBackToDefaults()
    {
        var result = AppSettingsToml.Read(
            "session_log_enabled = \"yes\"\ncheck_for_updates = 1\nhosted_config_url = 5\nhosted_config_last_hash = 7\n");

        Assert.True(result.IsValid);
        Assert.Equal(4, result.Warnings.Count);
        Assert.Equal(new AppSettings(), result.Settings);
    }

    [Fact]
    public void NonHttpsHostedUrl_InTheFile_IsIgnoredWithAWarning()
    {
        var result = AppSettingsToml.Read("hosted_config_url = \"http://example.com/a.toml\"\n");

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.Settings.HostedConfigUrl);
        Assert.Contains(result.Warnings, w => w.StartsWith("hosted_config_url"));
    }

    [Fact]
    public void Hash_IsStoredLowerCase()
    {
        var result = AppSettingsToml.Read($"hosted_config_last_hash = \"{Hash.ToUpperInvariant()}\"\n");

        Assert.Equal(Hash, result.Settings.HostedConfigLastHash);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]   // 63 characters
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")] // 65 characters
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]  // not hex
    public void Hash_ThatIsNotSixtyFourHexCharacters_IsClearedWithAWarning(string bad)
    {
        var result = AppSettingsToml.Read($"cache_ttl_days = 7\nhosted_config_last_hash = \"{bad}\"\n");

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.Settings.HostedConfigLastHash);
        Assert.Equal(7, result.Settings.CacheTtlDays);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("hosted_config_last_hash", warning);
    }

    [Fact]
    public void EmptyHash_IsFineWithoutAWarning()
    {
        var result = AppSettingsToml.Read("hosted_config_last_hash = \"\"\n");

        Assert.Empty(result.Warnings);
        Assert.Equal(string.Empty, result.Settings.HostedConfigLastHash);
    }

    [Fact]
    public void IsValidHash_AcceptsOnlySixtyFourHexCharacters()
    {
        Assert.True(AppSettingsToml.IsValidHash(Hash));
        Assert.True(AppSettingsToml.IsValidHash(Hash.ToUpperInvariant()));
        Assert.False(AppSettingsToml.IsValidHash(null));
        Assert.False(AppSettingsToml.IsValidHash(string.Empty));
        Assert.False(AppSettingsToml.IsValidHash(Hash.Substring(1)));
        Assert.False(AppSettingsToml.IsValidHash(Hash.Substring(1) + "z"));
    }

    private static readonly DateTimeOffset CheckTime = new(2026, 10, 22, 9, 30, 15, TimeSpan.Zero);

    [Fact]
    public void AutoCheck_WithoutTheKeys_IsOffAndNeverChecked_WithoutWarnings()
    {
        var result = AppSettingsToml.Read("format_version = 1\nhosted_config_url = \"https://example.com/a.toml\"\n");

        Assert.Empty(result.Warnings);
        Assert.False(result.Settings.HostedConfigAutoCheck);
        Assert.Null(result.Settings.HostedConfigLastCheck);
    }

    [Fact]
    public void AutoCheck_SaveThenLoad_RoundTrips_AsUtcText()
    {
        var path = _dir.File("settings.toml");
        var settings = new AppSettings
        {
            HostedConfigUrl = "https://example.com/a.toml",
            HostedConfigAutoCheck = true,
            HostedConfigLastCheck = CheckTime,
        };

        var saved = AppSettingsToml.Save(path, settings);
        var loaded = AppSettingsToml.Load(path);

        Assert.True(saved.Success, saved.Error);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(settings, loaded.Settings);
        var text = File.ReadAllText(path);
        Assert.Contains("hosted_config_auto_check = true", text);
        Assert.Contains("hosted_config_last_check = \"2026-10-22T09:30:15Z\"", text);
    }

    [Fact]
    public void AutoCheck_NeverChecked_WritesNoLastCheckLine()
    {
        var text = AppSettingsToml.Write(new AppSettings());

        Assert.Contains("hosted_config_auto_check = false", text);
        Assert.DoesNotContain("hosted_config_last_check", text);
    }

    [Fact]
    public void LastCheck_IsKeptInWholeSecondsAndUtc()
    {
        var local = new DateTimeOffset(2026, 10, 22, 11, 30, 15, 987, TimeSpan.FromHours(2));
        var text = AppSettingsToml.Write(new AppSettings { HostedConfigLastCheck = local });

        Assert.Contains("hosted_config_last_check = \"2026-10-22T09:30:15Z\"", text);
        Assert.Equal(CheckTime, AppSettingsToml.Read(text).Settings.HostedConfigLastCheck);
    }

    [Theory]
    [InlineData("2026-10-22T09:30:15Z")]
    [InlineData("2026-10-22T11:30:15+02:00")]
    [InlineData("2026-10-22T09:30:15")]   // no zone: taken as UTC
    [InlineData("2026-10-22 09:30:15Z")]
    public void LastCheck_ReadsIsoTimestamps(string value)
    {
        var result = AppSettingsToml.Read($"hosted_config_last_check = \"{value}\"\n");

        Assert.Empty(result.Warnings);
        Assert.Equal(CheckTime, result.Settings.HostedConfigLastCheck);
    }

    [Theory]
    [InlineData("hosted_config_last_check = \"yesterday\"")]
    [InlineData("hosted_config_last_check = 5")]
    public void LastCheck_Garbage_IsAWarning_AndMeansNeverChecked(string line)
    {
        var result = AppSettingsToml.Read("cache_ttl_days = 7\n" + line + "\n");

        Assert.True(result.IsValid);
        Assert.Null(result.Settings.HostedConfigLastCheck);
        Assert.Equal(7, result.Settings.CacheTtlDays);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("hosted_config_last_check", warning);
    }

    [Fact]
    public void LastCheck_EmptyText_MeansNeverCheckedWithoutAWarning()
    {
        var result = AppSettingsToml.Read("hosted_config_last_check = \"\"\n");

        Assert.Empty(result.Warnings);
        Assert.Null(result.Settings.HostedConfigLastCheck);
    }

    [Fact]
    public void AutoCheck_WrongType_IsAWarning_AndOff()
    {
        var result = AppSettingsToml.Read("hosted_config_auto_check = \"yes\"\n");

        Assert.True(result.IsValid);
        Assert.False(result.Settings.HostedConfigAutoCheck);
        Assert.StartsWith("hosted_config_auto_check", Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hosted_config_url = \"\"\n")]
    [InlineData("hosted_config_url = \"http://example.com/a.toml\"\n")]   // not https: the address is ignored
    public void AutoCheck_OnWithoutAValidAddress_IsSwitchedOffWithAWarning(string urlLine)
    {
        var result = AppSettingsToml.Read(urlLine + "hosted_config_auto_check = true\n");

        Assert.True(result.IsValid);
        Assert.False(result.Settings.HostedConfigAutoCheck);
        Assert.Contains(result.Warnings, w => w.StartsWith("hosted_config_auto_check"));
    }

    [Fact]
    public void AutoCheck_OnWithAValidAddress_StaysOn()
    {
        var result = AppSettingsToml.Read("hosted_config_url = \"https://example.com/a.toml\"\nhosted_config_auto_check = true\n");

        Assert.Empty(result.Warnings);
        Assert.True(result.Settings.HostedConfigAutoCheck);
    }

    [Fact]
    public void Save_WritesAThirtyWhenTheTtlIsNotSevenOrThirty()
    {
        var path = _dir.File("settings.toml");

        AppSettingsToml.Save(path, new AppSettings { CacheTtlDays = 14 });

        Assert.Equal(30, AppSettingsToml.Load(path).Settings.CacheTtlDays);
    }
}

public class LiveSettingsTests
{
    [Fact]
    public void CacheSettings_TtlCanChangeAtRuntime()
    {
        var settings = new CacheSettings();
        Assert.Equal(TimeSpan.FromDays(30), settings.Ttl);

        settings.TtlDays = 7;

        Assert.Equal(7, settings.TtlDays);
        Assert.Equal(TimeSpan.FromDays(7), settings.Ttl);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.TtlDays = 14);
        Assert.Equal(7, settings.TtlDays); // a refused value changes nothing
    }

    [Fact]
    public void FileLog_Enabled_StopsAndRestartsWriting()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var log = new FileLog(dir.Path, clock);
        Assert.True(log.Enabled);

        log.Write(clock.UtcNow, "one");
        log.Enabled = false;
        log.Write(clock.UtcNow, "two");
        log.Enabled = true;
        log.Write(clock.UtcNow, "three");

        var lines = File.ReadAllLines(log.CurrentFilePath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("  one", lines[0]);
        Assert.EndsWith("  three", lines[1]);
    }

    [Fact]
    public void FileLog_Disabled_CreatesNoFile_ButCleanupStillWorks()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var log = new FileLog(dir.Path, clock) { Enabled = false };
        var old = Path.Combine(dir.Path, "iris-2020-01-01.log");
        File.WriteAllText(old, "x");

        log.Write(clock.UtcNow, "ignored");
        var deleted = FileLog.DeleteOlderThan(dir.Path, TimeSpan.FromDays(30), clock.UtcNow);

        Assert.False(File.Exists(log.CurrentFilePath));
        Assert.Equal(1, deleted);
    }

    [Fact]
    public void TeeLog_KeepsTheOtherLogsWorking_WhenFileLogIsOff()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var file = new FileLog(dir.Path, clock) { Enabled = false };
        var memory = new ListLog();
        var tee = new TeeLog(memory, file);

        tee.Write(clock.UtcNow, "hello");

        Assert.Single(memory.Entries);
        Assert.False(File.Exists(file.CurrentFilePath));
    }
}

public class HostedConfigScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static AppSettings On(DateTimeOffset? lastCheck) => new()
    {
        HostedConfigUrl = "https://example.com/a.toml",
        HostedConfigAutoCheck = true,
        HostedConfigLastCheck = lastCheck,
    };

    [Fact]
    public void NeverChecked_IsDueAtOnce()
    {
        Assert.True(HostedConfigSchedule.IsDue(On(null), Now));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(23 * 60 + 59, false)]
    [InlineData(24 * 60, true)]       // exactly 24 hours counts
    [InlineData(24 * 60 + 1, true)]
    [InlineData(10 * 24 * 60, true)]
    public void IsDue_OnlyWhen24HoursHavePassed(int minutesAgo, bool due)
    {
        Assert.Equal(due, HostedConfigSchedule.IsDue(On(Now - TimeSpan.FromMinutes(minutesAgo)), Now));
    }

    [Fact]
    public void LastCheckInTheFuture_CountsAsDue()
    {
        Assert.True(HostedConfigSchedule.IsDue(On(Now + TimeSpan.FromDays(3)), Now));
    }

    [Fact]
    public void SwitchedOff_IsNeverDue()
    {
        Assert.False(HostedConfigSchedule.IsDue(On(null) with { HostedConfigAutoCheck = false }, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com/a.toml")]
    [InlineData("not a url")]
    [InlineData("https://user:pw@example.com/a.toml")]
    public void WithoutAValidAddress_IsNeverDue(string url)
    {
        Assert.False(HostedConfigSchedule.IsDue(On(null) with { HostedConfigUrl = url }, Now));
    }

    [Fact]
    public void IsUpdate_SameHashAsLastImport_IsNoUpdate_IgnoringCase()
    {
        var hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var settings = new AppSettings { HostedConfigLastHash = hash };

        Assert.False(HostedConfigSchedule.IsUpdate(settings, hash));
        Assert.False(HostedConfigSchedule.IsUpdate(settings, hash.ToUpperInvariant()));
        Assert.True(HostedConfigSchedule.IsUpdate(settings, new string('a', 64)));
    }

    [Fact]
    public void IsUpdate_NothingImportedYet_IsAnUpdate()
    {
        Assert.True(HostedConfigSchedule.IsUpdate(new AppSettings(), new string('a', 64)));
    }

    [Fact]
    public void Intervals_Are24HoursAndTenMinutes()
    {
        Assert.Equal(TimeSpan.FromHours(24), HostedConfigSchedule.Interval);
        Assert.Equal(TimeSpan.FromMinutes(10), HostedConfigSchedule.WakeInterval);
    }
}
