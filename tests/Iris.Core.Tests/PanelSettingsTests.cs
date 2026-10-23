using Iris.Core.Settings;
using Xunit;

namespace Iris.Core.Tests;

public class PanelSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Defaults_AreAsSpecified()
    {
        var s = new AppSettings();

        Assert.True(s.PanelEnabled);
        Assert.Equal(16, s.PanelWidthPercent);
        Assert.Equal(18, s.PanelTextSize);
        Assert.Equal(10, s.PanelRightGapPercent);
        Assert.Equal(15, s.PanelTopGapPercent);
    }

    [Fact]
    public void MissingKeys_GiveDefaultsWithoutWarnings()
    {
        var result = AppSettingsToml.Read("format_version = 1\ncache_ttl_days = 7\n");

        Assert.Empty(result.Warnings);
        Assert.Equal(new AppSettings { CacheTtlDays = 7 }, result.Settings);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = _dir.File("settings.toml");
        var settings = new AppSettings
        {
            PanelEnabled = false,
            PanelWidthPercent = 25,
            PanelTextSize = 32,
            PanelRightGapPercent = 0,
            PanelTopGapPercent = 60,
        };

        var saved = AppSettingsToml.Save(path, settings);
        var loaded = AppSettingsToml.Load(path);

        Assert.True(saved.Success, saved.Error);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(settings, loaded.Settings);
        var text = File.ReadAllText(path);
        Assert.Contains("panel_enabled = false", text);
        Assert.Contains("panel_width_percent = 25", text);
        Assert.Contains("panel_text_size = 32", text);
        Assert.Contains("panel_right_gap_percent = 0", text);
        Assert.Contains("panel_top_gap_percent = 60", text);
    }

    [Theory]
    [InlineData(8, 10, 0, 0)]
    [InlineData(40, 40, 40, 60)]
    public void RangeLimits_AreAccepted(int width, int text, int rightGap, int topGap)
    {
        var result = AppSettingsToml.Read(
            $"panel_width_percent = {width}\npanel_text_size = {text}\npanel_right_gap_percent = {rightGap}\npanel_top_gap_percent = {topGap}\n");

        Assert.Empty(result.Warnings);
        Assert.Equal(width, result.Settings.PanelWidthPercent);
        Assert.Equal(text, result.Settings.PanelTextSize);
        Assert.Equal(rightGap, result.Settings.PanelRightGapPercent);
        Assert.Equal(topGap, result.Settings.PanelTopGapPercent);
    }

    [Theory]
    [InlineData("panel_width_percent = 7", "panel_width_percent", 16)]
    [InlineData("panel_width_percent = 41", "panel_width_percent", 16)]
    [InlineData("panel_text_size = 9", "panel_text_size", 18)]
    [InlineData("panel_text_size = 41", "panel_text_size", 18)]
    [InlineData("panel_right_gap_percent = -1", "panel_right_gap_percent", 10)]
    [InlineData("panel_right_gap_percent = 41", "panel_right_gap_percent", 10)]
    [InlineData("panel_top_gap_percent = 61", "panel_top_gap_percent", 15)]
    [InlineData("panel_top_gap_percent = \"15\"", "panel_top_gap_percent", 15)]
    [InlineData("panel_width_percent = 16.5", "panel_width_percent", 16)]
    public void OutOfRangeOrWrongType_BecomesTheDefaultWithAWarning(string line, string key, int expected)
    {
        var result = AppSettingsToml.Read("format_version = 1\n" + line + "\nsession_log_enabled = false\n");

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.StartsWith(key));
        Assert.Single(result.Warnings);
        Assert.False(result.Settings.SessionLogEnabled); // the other settings are still read
        var actual = key switch
        {
            "panel_width_percent" => result.Settings.PanelWidthPercent,
            "panel_text_size" => result.Settings.PanelTextSize,
            "panel_right_gap_percent" => result.Settings.PanelRightGapPercent,
            _ => result.Settings.PanelTopGapPercent,
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WrongTypedEnabled_WarnsAndKeepsTheDefault()
    {
        var result = AppSettingsToml.Read("panel_enabled = \"no\"\n");

        Assert.True(result.IsValid);
        Assert.StartsWith("panel_enabled", Assert.Single(result.Warnings));
        Assert.True(result.Settings.PanelEnabled);
    }

    [Fact]
    public void PanelKeys_AreKnownKeys_NotWarnedAbout()
    {
        var result = AppSettingsToml.Read(
            "panel_enabled = true\npanel_width_percent = 16\npanel_text_size = 18\npanel_right_gap_percent = 10\npanel_top_gap_percent = 15\n");

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Write_NeverWritesAValueTheReaderWouldReject()
    {
        var text = AppSettingsToml.Write(new AppSettings { PanelWidthPercent = 500, PanelTextSize = -3 });

        var back = AppSettingsToml.Read(text);

        Assert.Empty(back.Warnings);
        Assert.Equal(AppSettings.PanelWidthPercentMax, back.Settings.PanelWidthPercent);
        Assert.Equal(AppSettings.PanelTextSizeMin, back.Settings.PanelTextSize);
    }
}
