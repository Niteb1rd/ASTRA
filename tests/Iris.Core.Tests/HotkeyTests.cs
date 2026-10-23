using Iris.Core.Overlay;
using Iris.Core.Settings;
using Xunit;

namespace Iris.Core.Tests;

public class HotkeyTextTests
{
    [Theory]
    [InlineData("Ctrl+Shift+O", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4F)]
    [InlineData("Alt+F9", HotkeyModifiers.Alt, 0x78)]
    [InlineData("Win+Num5", HotkeyModifiers.Windows, 0x65)]
    [InlineData("Ctrl+Alt+Shift+Win+Delete", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0x2E)]
    [InlineData("Ctrl+0", HotkeyModifiers.Control, 0x30)]
    [InlineData("Shift+PageDown", HotkeyModifiers.Shift, 0x22)]
    [InlineData("O", HotkeyModifiers.None, 0x4F)]
    [InlineData("F5", HotkeyModifiers.None, 0x74)]
    [InlineData("Shift+O", HotkeyModifiers.Shift, 0x4F)]
    public void Valid_IsParsed(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(HotkeyText.TryParse(text, out var hotkey, out var error), error);
        Assert.Equal(modifiers, hotkey!.Modifiers);
        Assert.Equal(key, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("ctrl+shift+o", "Ctrl+Shift+O")]
    [InlineData("O+shift+CONTROL", "Ctrl+Shift+O")]
    [InlineData(" Shift + Alt + f1 ", "Alt+Shift+F1")]
    [InlineData("Windows+Del", "Win+Delete")]
    [InlineData("Ctrl+Return", "Ctrl+Enter")]
    public void Format_UsesTheStandardSpellingAndOrder(string text, string expected)
    {
        Assert.Equal(expected, HotkeyText.Normalize(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Shift")]       // no key
    [InlineData("Ctrl+")]
    [InlineData("+O")]
    [InlineData("Ctrl+Ctrl+O")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+;")]
    [InlineData("Ctrl+F25")]
    [InlineData("Banana")]
    public void Invalid_IsRejectedWithAReason(string text)
    {
        Assert.False(HotkeyText.TryParse(text, out var hotkey, out var error));
        Assert.Null(hotkey);
        Assert.NotEmpty(error);
        Assert.Equal(string.Empty, HotkeyText.Normalize(text));
    }

    [Theory]
    [InlineData("O")]
    [InlineData("5")]
    [InlineData("Num3")]
    [InlineData("Shift+O")]
    [InlineData("shift+a")]
    [InlineData("Ctrl+O")]
    [InlineData("Alt+5")]
    [InlineData("Win+Q")]
    [InlineData("F5")]
    [InlineData("Space")]
    [InlineData("Up")]
    public void LettersDigitsAndOtherKeys_AreAllowedWithOrWithoutModifiers(string text)
    {
        Assert.True(HotkeyText.TryParse(text, out _, out var error), error);
    }

    [Fact]
    public void SingleKey_FormatsWithoutAPlus_AndRoundTrips()
    {
        Assert.Equal("O", HotkeyText.Normalize("o"));
        Assert.Equal("F9", HotkeyText.Normalize(" f9 "));
        Assert.Equal("Space", HotkeyText.Format(new Hotkey(HotkeyModifiers.None, 0x20)));
    }

    [Fact]
    public void TryCreate_AllowsNoModifier_AndShiftAlone_WithAnyKind()
    {
        Assert.True(HotkeyText.TryCreate(HotkeyModifiers.None, 0x4F, out var plain));
        Assert.Equal(new Hotkey(HotkeyModifiers.None, 0x4F), plain);
        Assert.True(HotkeyText.TryCreate(HotkeyModifiers.Shift, 0x4F, out _));   // Shift+O
        Assert.True(HotkeyText.TryCreate(HotkeyModifiers.Shift, 0x35, out _));   // Shift+5
        Assert.True(HotkeyText.TryCreate(HotkeyModifiers.None, 0x74, out _));    // F5
    }

    [Fact]
    public void TryCreate_UnsupportedKey_HasItsOwnMessage()
    {
        Assert.False(HotkeyText.TryCreate(HotkeyModifiers.None, 0x1B, out var hotkey, out var badKey)); // Esc
        Assert.Null(hotkey);
        Assert.Contains("cannot be used", badKey);
    }

    [Fact]
    public void FormatThenParse_RoundTrips_ForEverySupportedKey()
    {
        var count = 0;
        for (var vk = 0; vk < 256; vk++)
        {
            if (!HotkeyText.IsSupportedKey(vk))
                continue;
            count++;
            Assert.True(HotkeyText.TryCreate(HotkeyModifiers.Control | HotkeyModifiers.Alt, vk, out var made));
            var text = HotkeyText.Format(made!);
            Assert.True(HotkeyText.TryParse(text, out var back, out var error), text + ": " + error);
            Assert.Equal(made, back);
        }
        Assert.Equal(26 + 10 + 10 + 24 + 14, count);
    }

    [Fact]
    public void TryCreate_NeedsASupportedKey()
    {
        Assert.False(HotkeyText.TryCreate(HotkeyModifiers.None, 0x1B, out _)); // Esc
        Assert.False(HotkeyText.TryCreate(HotkeyModifiers.Control, 0x1B, out _)); // Esc
        Assert.False(HotkeyText.TryCreate(HotkeyModifiers.Control, 0x10, out _)); // Shift key itself
        Assert.True(HotkeyText.TryCreate(HotkeyModifiers.Control, 0x4F, out _));
    }

    [Fact]
    public void ModifierValues_MatchWindowsMod()
    {
        Assert.Equal(1, (int)HotkeyModifiers.Alt);
        Assert.Equal(2, (int)HotkeyModifiers.Control);
        Assert.Equal(4, (int)HotkeyModifiers.Shift);
        Assert.Equal(8, (int)HotkeyModifiers.Windows);
    }
}

public class HotkeySettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Defaults_NoHotkeys_OverlayNotDisabled()
    {
        var s = new AppSettings();

        Assert.False(s.OverlayDisabled);
        Assert.Equal(string.Empty, s.HotkeyToggleOverlay);
        Assert.Equal(string.Empty, s.HotkeyTogglePanel);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = _dir.File("settings.toml");
        var settings = new AppSettings
        {
            OverlayDisabled = true,
            PanelEnabled = false,
            HotkeyToggleOverlay = "Ctrl+Shift+O",
            HotkeyTogglePanel = "Alt+F9",
        };

        var saved = AppSettingsToml.Save(path, settings);
        var loaded = AppSettingsToml.Load(path);

        Assert.True(saved.Success, saved.Error);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(settings, loaded.Settings);
        var text = File.ReadAllText(path);
        Assert.Contains("overlay_disabled = true", text);
        Assert.Contains("hotkey_toggle_overlay = \"Ctrl+Shift+O\"", text);
        Assert.Contains("hotkey_toggle_panel = \"Alt+F9\"", text);
    }

    [Fact]
    public void HotkeyTexts_AreNormalisedWhenRead()
    {
        var result = AppSettingsToml.Read("hotkey_toggle_overlay = \"shift+control+o\"\n");

        Assert.Empty(result.Warnings);
        Assert.Equal("Ctrl+Shift+O", result.Settings.HotkeyToggleOverlay);
    }

    [Theory]
    [InlineData("hotkey_toggle_overlay = \"Ctrl+Esc\"", "hotkey_toggle_overlay")]
    [InlineData("hotkey_toggle_overlay = \"Ctrl+Shift\"", "hotkey_toggle_overlay")] // no key
    [InlineData("hotkey_toggle_panel = \"nonsense\"", "hotkey_toggle_panel")]
    [InlineData("hotkey_toggle_panel = 5", "hotkey_toggle_panel")]
    public void InvalidHotkey_BecomesEmptyWithAWarning(string line, string key)
    {
        var result = AppSettingsToml.Read("format_version = 1\n" + line + "\noverlay_disabled = true\n");

        Assert.True(result.IsValid);
        Assert.StartsWith(key, Assert.Single(result.Warnings));
        Assert.Equal(string.Empty, result.Settings.HotkeyToggleOverlay);
        Assert.Equal(string.Empty, result.Settings.HotkeyTogglePanel);
        Assert.True(result.Settings.OverlayDisabled); // the other settings are still read
    }

    [Fact]
    public void SingleKeyHotkey_IsReadWithoutAWarning()
    {
        var result = AppSettingsToml.Read("hotkey_toggle_overlay = \"f9\"\n");

        Assert.Empty(result.Warnings);
        Assert.Equal("F9", result.Settings.HotkeyToggleOverlay);
    }

    [Fact]
    public void EmptyHotkey_IsFineWithoutAWarning()
    {
        var result = AppSettingsToml.Read("hotkey_toggle_overlay = \"\"\nhotkey_toggle_panel = \"  \"\n");

        Assert.Empty(result.Warnings);
        Assert.Equal(new AppSettings(), result.Settings);
    }

    [Fact]
    public void WrongTypedOverlayDisabled_WarnsAndKeepsTheDefault()
    {
        var result = AppSettingsToml.Read("overlay_disabled = \"yes\"\n");

        Assert.StartsWith("overlay_disabled", Assert.Single(result.Warnings));
        Assert.False(result.Settings.OverlayDisabled);
    }

    [Fact]
    public void Write_NeverWritesAnInvalidHotkey()
    {
        var text = AppSettingsToml.Write(new AppSettings { HotkeyToggleOverlay = "garbage", HotkeyTogglePanel = "ctrl+p" });

        var back = AppSettingsToml.Read(text);

        Assert.Empty(back.Warnings);
        Assert.Equal(string.Empty, back.Settings.HotkeyToggleOverlay);
        Assert.Equal("Ctrl+P", back.Settings.HotkeyTogglePanel);
    }
}

public class ToastLayoutTests
{
    [Fact]
    public void Toast_SitsJustAboveThePanelTop_OneLineHigh()
    {
        var toast = PanelLayout.ComputeToast(2560, 1440, new AppSettings())!;

        Assert.Equal(2304.0, toast.Rect.Right, 6);
        Assert.Equal(0.16 * 2560, toast.Rect.Width, 6);
        Assert.Equal(18.0, toast.FontSize, 6);
        Assert.Equal(toast.LineHeight + 2 * toast.PaddingY, toast.Rect.Height, 6);
        // the panel starts at 15 % of 1440 = 216; the box ends one entry gap (0.4 x font size) above it
        Assert.Equal(216.0 - 0.4 * 18.0, toast.Rect.Bottom, 6);
        Assert.True(toast.Rect.Y > 0);
    }

    [Fact]
    public void Toast_DoesNotOverlapTheFirstPanelEntry()
    {
        var entries = new List<PanelEntry> { new("Abcd", Iris.Core.Social.MatchKind.NoMatch, Iris.Core.Config.OverlayColor.Blue, "Orgs: none found") };
        var settings = new AppSettings();

        var panel = PanelLayout.Compute(entries, 2560, 1440, settings);
        var toast = PanelLayout.ComputeToast(2560, 1440, settings)!;

        Assert.True(toast.Rect.Bottom < panel.Slots[0].Rect.Y);
    }

    [Fact]
    public void Toast_IsShownEvenWhenThePanelIsOff_AndFollowsTheSettings()
    {
        var settings = new AppSettings { PanelEnabled = false, PanelWidthPercent = 20, PanelRightGapPercent = 0, PanelTopGapPercent = 30 };

        var toast = PanelLayout.ComputeToast(1000, 800, settings)!;

        Assert.Equal(1000.0, toast.Rect.Right, 6);
        Assert.Equal(200.0, toast.Rect.Width, 6);
        Assert.Equal(240.0 - 0.4 * toast.FontSize, toast.Rect.Bottom, 6);
    }

    [Fact]
    public void Toast_WithNoRoomAboveThePanel_StaysOnScreenAtTheTop()
    {
        var settings = new AppSettings { PanelTopGapPercent = 0 };

        var toast = PanelLayout.ComputeToast(1000, 800, settings)!;

        Assert.Equal(0.0, toast.Rect.Y, 6);
    }

    [Fact]
    public void Toast_InAVeryFlatWindow_NeverLeavesTheWindow()
    {
        var toast = PanelLayout.ComputeToast(1000, 20, new AppSettings { PanelTopGapPercent = 60 })!;

        Assert.True(toast.Rect.Y >= 0);
        Assert.True(toast.Rect.Bottom <= 20 || toast.Rect.Y == 0);
    }

    [Fact]
    public void Toast_HasTheSameTextSizeAsThePanel()
    {
        var entries = new List<PanelEntry> { new("Abcd", Iris.Core.Social.MatchKind.NoMatch, Iris.Core.Config.OverlayColor.Blue, "Orgs: none found") };
        var panel = PanelLayout.Compute(entries, 3840, 2160, new AppSettings());

        var toast = PanelLayout.ComputeToast(3840, 2160, new AppSettings())!;

        Assert.Equal(panel.FontSize, toast.FontSize, 6);
        Assert.Equal(panel.Slots[0].Rect.X, toast.Rect.X, 6);
    }

    [Fact]
    public void Toast_UnusableWindow_IsNull()
    {
        Assert.Null(PanelLayout.ComputeToast(0, 100, new AppSettings()));
    }
}
