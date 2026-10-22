using Astra.Core.Cache;
using Astra.Core.Logging;
using Astra.Core.Settings;
using Xunit;

namespace Astra.Core.Tests;

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
            HostedConfigUrl = "https://example.com/cfg/astra-config.toml?token=a&b=c",
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
        var old = Path.Combine(dir.Path, "astra-2020-01-01.log");
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
