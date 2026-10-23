using Iris.Core.Config;
using Iris.Core.Overlay;
using Iris.Core.Settings;
using Iris.Core.Social;
using Xunit;

namespace Iris.Core.Tests;

public class PanelLayoutTests
{
    private static PanelEntry Entry(string handle, MatchKind kind = MatchKind.NoMatch) =>
        new(handle, kind, OverlayColor.Blue, "Orgs: none found");

    private static List<PanelEntry> Entries(int count) =>
        Enumerable.Range(1, count).Select(i => Entry("Player" + i)).ToList();

    [Fact]
    public void Defaults_At1440p_PlacePanelByThePercentages()
    {
        var layout = PanelLayout.Compute(Entries(2), 2560, 1440, new AppSettings());

        Assert.Equal(2, layout.Slots.Count);
        var first = layout.Slots[0].Rect;
        Assert.Equal(2560 - 256, first.Right, 6);        // right edge = width - 10% of width
        Assert.Equal(0.16 * 2560, first.Width, 6);        // 16% of the width
        Assert.Equal(0.15 * 1440, first.Y, 6);            // top = 15% of the height
        Assert.Equal(18.0, layout.FontSize, 6);           // 18 px at 1440p
        Assert.Null(layout.MoreText);
    }

    [Fact]
    public void EntriesStackDownward_WithAGapBetweenRectangles_AndInOrder()
    {
        var entries = new List<PanelEntry> { Entry("Aaaa"), Entry("Bbbb"), Entry("Cccc") };

        var layout = PanelLayout.Compute(entries, 2560, 1440, new AppSettings());

        Assert.Equal(new[] { "Aaaa", "Bbbb", "Cccc" }, layout.Slots.Select(s => s.Entry.Handle).ToArray());
        for (var i = 1; i < layout.Slots.Count; i++)
        {
            var above = layout.Slots[i - 1].Rect;
            var below = layout.Slots[i].Rect;
            Assert.True(below.Y > above.Bottom, "rectangles must not touch");
            Assert.Equal(above.Height, below.Height, 6);
            Assert.Equal(above.X, below.X, 6);
        }
    }

    [Fact]
    public void TextSize_ScalesLinearlyWithWindowHeight()
    {
        Assert.Equal(18.0, PanelLayout.Compute(Entries(1), 2560, 1440, new AppSettings()).FontSize, 6);
        Assert.Equal(27.0, PanelLayout.Compute(Entries(1), 3840, 2160, new AppSettings()).FontSize, 6);
        Assert.Equal(13.5, PanelLayout.Compute(Entries(1), 1920, 1080, new AppSettings()).FontSize, 6);
        Assert.Equal(24.0, PanelLayout.Compute(Entries(1), 1920, 1080, new AppSettings { PanelTextSize = 32 }).FontSize, 6);
    }

    [Fact]
    public void RectangleHeight_FollowsTheTextSize()
    {
        var small = PanelLayout.Compute(Entries(1), 2560, 1440, new AppSettings { PanelTextSize = 10 }).Slots[0].Rect.Height;
        var big = PanelLayout.Compute(Entries(1), 2560, 1440, new AppSettings { PanelTextSize = 40 }).Slots[0].Rect.Height;

        Assert.Equal(4.0, big / small, 6);
    }

    [Fact]
    public void WidthAndGaps_FollowTheSettings()
    {
        var settings = new AppSettings { PanelWidthPercent = 20, PanelRightGapPercent = 0, PanelTopGapPercent = 0 };

        var rect = PanelLayout.Compute(Entries(1), 1000, 800, settings).Slots[0].Rect;

        Assert.Equal(1000.0, rect.Right, 6);
        Assert.Equal(200.0, rect.Width, 6);
        Assert.Equal(0.0, rect.Y, 6);
    }

    [Fact]
    public void ExtremeSettings_KeepThePanelOnScreen()
    {
        var settings = new AppSettings { PanelWidthPercent = 40, PanelRightGapPercent = 40 };

        var rect = PanelLayout.Compute(Entries(1), 1000, 800, settings).Slots[0].Rect;

        Assert.True(rect.X >= 0);
        Assert.True(rect.Right <= 1000);
    }

    [Fact]
    public void Disabled_OrNoEntries_GivesNothing()
    {
        Assert.True(PanelLayout.Compute(Entries(3), 2560, 1440, new AppSettings { PanelEnabled = false }).IsEmpty);
        Assert.True(PanelLayout.Compute(new List<PanelEntry>(), 2560, 1440, new AppSettings()).IsEmpty);
        Assert.True(PanelLayout.Compute(Entries(3), 0, 1440, new AppSettings()).IsEmpty);
    }

    [Fact]
    public void ExactlyFitting_ShowsEverythingWithoutAMoreLine()
    {
        // 1440p defaults: 15 entries end at 216 + 1018.8 = 1234.8, above the 1296 limit (the bottom 10% stays free).
        var layout = PanelLayout.Compute(Entries(15), 2560, 1440, new AppSettings());

        Assert.Equal(15, layout.Slots.Count);
        Assert.Null(layout.MoreText);
        Assert.True(layout.Slots[^1].Rect.Bottom <= 1440 * 0.9);
    }

    [Fact]
    public void OneTooMany_ShowsFifteenAndPlusOneMore()
    {
        // 16 entries would end at 216 + 1087.2 = 1303.2, past the limit; 15 entries plus the "+1 more" line fit.
        var layout = PanelLayout.Compute(Entries(16), 2560, 1440, new AppSettings());

        Assert.Equal(15, layout.Slots.Count);
        Assert.Equal("+1 more", layout.MoreText);
    }

    [Fact]
    public void ManyEntries_StopBeforeTheBottomTenPercent_AndCountTheRest()
    {
        var layout = PanelLayout.Compute(Entries(20), 2560, 1440, new AppSettings());

        var limit = 1440 * 0.9;
        Assert.All(layout.Slots, s => Assert.True(s.Rect.Bottom <= limit));
        Assert.Equal($"+{20 - layout.Slots.Count} more", layout.MoreText);
        Assert.True(layout.MoreRect.Y > layout.Slots[^1].Rect.Bottom);
        Assert.True(layout.MoreRect.Bottom <= limit);
        Assert.Equal(layout.Slots[0].Rect.X, layout.MoreRect.X, 6);
    }

    [Fact]
    public void LowStartAndBigText_ShowOnlyWhatFits()
    {
        var settings = new AppSettings { PanelTopGapPercent = 60, PanelTextSize = 40 };

        var layout = PanelLayout.Compute(Entries(10), 2560, 1440, settings);

        Assert.Equal(2, layout.Slots.Count);
        Assert.Equal("+8 more", layout.MoreText);
    }
}

public class OverlayModelBuilderTests
{
    private static SocialConfig Config()
    {
        var c = new SocialConfig();
        c.Players.Add(new PlayerEntry("Alice", OverlayColor.Cyan));
        c.Orgs.Add(new OrgEntry("HEKATE", 1, OverlayColor.Red));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 1, OverlayColor.Green));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoOrg, null, 2, OverlayColor.Orange));
        NoMatchRule.Ensure(c);
        return c;
    }

    private static readonly Dictionary<string, IReadOnlyList<OrgMembership>> Cache = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bob"] = new[] { new OrgMembership("AVOCADO", true, "PMC") },
        ["Carl"] = new[] { new OrgMembership("HEKATE", true) },
        ["Dan"] = new[] { new OrgMembership("OTHER", true) },
        ["Eve"] = Array.Empty<OrgMembership>(),
    };

    private static IReadOnlyList<OrgMembership>? Lookup(string handle) => Cache.TryGetValue(handle, out var orgs) ? orgs : null;

    private static DetectedPlayer Seen(string handle, double x = 0) => new(handle, new PixelRect(x, 100, 150, 40));

    [Fact]
    public void PanelOrder_IsPlayerList_OrgList_OrgAttribute_NoMatch_TiesByFirstSeen()
    {
        var detections = new[] { Seen("Zed"), Seen("Bob"), Seen("Carl"), Seen("Alice"), Seen("Dan"), Seen("Eve") };

        var model = OverlayModelBuilder.Build(detections, Config(), Lookup);

        Assert.Equal(new[] { "Alice", "Carl", "Bob", "Eve", "Zed", "Dan" }, model.Panel.Select(p => p.Handle).ToArray());
        Assert.Equal(
            new[] { MatchKind.PlayerList, MatchKind.OrgList, MatchKind.OrgAttribute, MatchKind.OrgAttribute, MatchKind.NoMatch, MatchKind.NoMatch },
            model.Panel.Select(p => p.Kind).ToArray());
    }

    [Fact]
    public void EveryBracketedPlayer_IsInThePanel_NoMatchIncluded()
    {
        var detections = new[] { Seen("Dan", 0), Seen("Zed", 200), Seen("Carl", 400) };

        var model = OverlayModelBuilder.Build(detections, Config(), Lookup);

        Assert.Equal(3, model.Brackets.Count);
        Assert.Equal(3, model.Panel.Count);
        Assert.Contains(model.Panel, p => p.Handle == "Dan" && p.Kind == MatchKind.NoMatch && p.Color == OverlayColor.Blue);
        Assert.Equal(detections.Select(d => d.Box).ToArray(), model.Brackets.Select(b => b.Box).ToArray());
    }

    [Fact]
    public void PlayerNotInTheCache_ShowsNotLookedUp_AndIsNoMatch()
    {
        var model = OverlayModelBuilder.Build(new[] { Seen("Zed") }, Config(), Lookup);

        var entry = Assert.Single(model.Panel);
        Assert.Equal("Orgs: not looked up", entry.OrgsLine);
        Assert.Equal(PanelText.NotLookedUp, entry.OrgsLine);
        Assert.Equal(MatchKind.NoMatch, entry.Kind); // not "No Org": nothing is known about the player's orgs
    }

    [Fact]
    public void ListedPlayerNotInTheCache_IsStillOnThePlayerList_WithNotLookedUp()
    {
        var model = OverlayModelBuilder.Build(new[] { Seen("alice") }, Config(), Lookup);

        var entry = Assert.Single(model.Panel);
        Assert.Equal(MatchKind.PlayerList, entry.Kind);
        Assert.Equal(OverlayColor.Cyan, entry.Color);
        Assert.Equal("Orgs: not looked up", entry.OrgsLine);
    }

    [Fact]
    public void CachedPlayers_GetTheNormalOrgsLine()
    {
        var model = OverlayModelBuilder.Build(new[] { Seen("Bob"), Seen("Dan"), Seen("Eve") }, Config(), Lookup);

        Assert.Equal("Orgs: AVOCADO* (PMC)", model.Panel.Single(p => p.Handle == "Bob").OrgsLine);
        Assert.Equal("Orgs: OTHER*", model.Panel.Single(p => p.Handle == "Dan").OrgsLine);
        Assert.Equal("Orgs: none found", model.Panel.Single(p => p.Handle == "Eve").OrgsLine);
    }

    [Fact]
    public void ANameSeenTwice_HasOnePanelEntryButTwoBrackets()
    {
        var model = OverlayModelBuilder.Build(new[] { Seen("Zed", 0), Seen("zed", 300) }, Config(), Lookup);

        Assert.Equal(2, model.Brackets.Count);
        Assert.Single(model.Panel);
    }

    [Fact]
    public void NoDetections_GiveAnEmptyModel()
    {
        var model = OverlayModelBuilder.Build(Array.Empty<DetectedPlayer>(), Config(), Lookup);

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void ResolveUnknown_IgnoresOrgRules()
    {
        var match = ColorResolver.ResolveUnknown("Nobody", Config());

        Assert.Equal(MatchKind.NoMatch, match.Kind);
        Assert.Equal(OverlayColor.Blue, match.Color);
    }
}

public class TestOverlayBuilderTests
{
    private static SocialConfig Config(bool withRule = true, int players = 4, int orgs = 3)
    {
        var c = new SocialConfig();
        for (var i = 1; i <= players; i++)
            c.Players.Add(new PlayerEntry("Listed" + i, OverlayColor.Cyan));
        for (var i = 1; i <= orgs; i++)
            c.Orgs.Add(new OrgEntry("ORG" + i, i, OverlayColor.Red));
        if (withRule)
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 1, OverlayColor.Green));
        NoMatchRule.Ensure(c);
        return c;
    }

    private static IReadOnlyList<OrgMembership>? NothingCached(string handle) => null;

    [Fact]
    public void FullLists_GiveThreeTwoOneTwo()
    {
        var model = TestOverlayBuilder.Build(Config(), NothingCached, 2560, 1440);

        Assert.Equal(3, model.Brackets.Count(b => b.Kind == MatchKind.PlayerList));
        Assert.Equal(2, model.Brackets.Count(b => b.Kind == MatchKind.OrgList));
        Assert.Equal(1, model.Brackets.Count(b => b.Kind == MatchKind.OrgAttribute));
        Assert.Equal(2, model.Brackets.Count(b => b.Kind == MatchKind.NoMatch));
        Assert.Equal(8, model.Panel.Count);
    }

    [Fact]
    public void NoMatchExamples_ShowNotLookedUp()
    {
        var model = TestOverlayBuilder.Build(Config(), NothingCached, 2560, 1440);

        var pilots = model.Panel.Where(p => p.Kind == MatchKind.NoMatch).ToList();
        Assert.Equal(new[] { "TestPilot01", "TestPilot02" }, pilots.Select(p => p.Handle).ToArray());
        Assert.All(pilots, p => Assert.Equal("Orgs: not looked up", p.OrgsLine));
    }

    [Fact]
    public void OrgExamples_UseSidsFromTheOrgList()
    {
        var model = TestOverlayBuilder.Build(Config(), NothingCached, 2560, 1440);

        var orgPlayers = model.Panel.Where(p => p.Kind == MatchKind.OrgList).ToList();
        Assert.Equal(2, orgPlayers.Count);
        Assert.Contains("ORG1", orgPlayers[0].OrgsLine);
        Assert.Contains("ORG2", orgPlayers[1].OrgsLine);
    }

    [Fact]
    public void ListedPlayers_UseTheCacheWhenItHasThem()
    {
        IReadOnlyList<OrgMembership>? Cached(string h) => h == "Listed1" ? new[] { new OrgMembership("HEKATE", true) } : null;

        var model = TestOverlayBuilder.Build(Config(), Cached, 2560, 1440);

        Assert.Equal("Orgs: HEKATE*", model.Panel.Single(p => p.Handle == "Listed1").OrgsLine);
        Assert.Equal("Orgs: not looked up", model.Panel.Single(p => p.Handle == "Listed2").OrgsLine);
    }

    [Fact]
    public void EmptyLists_StillGiveTheTwoNoMatchExamples()
    {
        var model = TestOverlayBuilder.Build(Config(withRule: false, players: 0, orgs: 0), NothingCached, 1920, 1080);

        Assert.Equal(2, model.Brackets.Count);
        Assert.All(model.Brackets, b => Assert.Equal(MatchKind.NoMatch, b.Kind));
    }

    [Fact]
    public void NoRule_MeansNoAttributeExample()
    {
        var model = TestOverlayBuilder.Build(Config(withRule: false), NothingCached, 2560, 1440);

        Assert.DoesNotContain(model.Brackets, b => b.Kind == MatchKind.OrgAttribute);
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Boxes_StayInsideTheScanRegion_AndDoNotOverlap(double width, double height)
    {
        var model = TestOverlayBuilder.Build(Config(), NothingCached, width, height);

        Assert.Equal(8, model.Brackets.Count);
        foreach (var box in model.Brackets.Select(b => b.Box))
        {
            Assert.True(box.X >= width * 0.15 - 1e-6);
            Assert.True(box.Right <= width * 0.85 + 1e-6);
            Assert.True(box.Y >= height * 0.10 - 1e-6);
            Assert.True(box.Bottom <= height * 0.70 + 1e-6);
            Assert.True(box.Width > box.Height, "a nameplate is wider than tall");
        }

        for (var i = 0; i < model.Brackets.Count; i++)
        {
            for (var j = i + 1; j < model.Brackets.Count; j++)
            {
                var a = model.Brackets[i].Box;
                var b = model.Brackets[j].Box;
                var apart = a.Right <= b.X || b.Right <= a.X || a.Bottom <= b.Y || b.Bottom <= a.Y;
                Assert.True(apart, $"boxes {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void BoxSize_ScalesWithScreenHeight()
    {
        var small = TestOverlayBuilder.BoxFor(0, "TestPilot01", 1920, 1080);
        var big = TestOverlayBuilder.BoxFor(0, "TestPilot01", 3840, 2160);

        Assert.Equal(2.0, big.Height / small.Height, 6);
    }
}

public class GameWindowRulesTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);
    private const uint Popup = 0x80000000;   // WS_POPUP
    private const uint Visible = 0x10000000; // WS_VISIBLE

    [Fact]
    public void PopupWindowCoveringTheMonitor_IsBorderless()
    {
        Assert.True(GameWindowRules.IsBorderless(Monitor, Monitor, Popup | Visible));
    }

    [Fact]
    public void BorderlessOnASecondMonitor_IsBorderless()
    {
        var second = new PixelRect(2560, -200, 1920, 1080);

        Assert.True(GameWindowRules.IsBorderless(second, second, Popup | Visible));
    }

    [Theory]
    [InlineData(0x00C00000u)] // WS_CAPTION
    [InlineData(0x00800000u)] // WS_BORDER alone
    [InlineData(0x00400000u)] // WS_DLGFRAME alone
    [InlineData(0x00040000u)] // WS_THICKFRAME
    [InlineData(0x00CF0000u)] // WS_OVERLAPPEDWINDOW-like
    public void CaptionOrFrameStyle_IsNotBorderless(uint extra)
    {
        Assert.False(GameWindowRules.IsBorderless(Monitor, Monitor, Popup | Visible | extra));
    }

    [Fact]
    public void WindowThatDoesNotCoverTheMonitor_IsNotBorderless()
    {
        Assert.False(GameWindowRules.IsBorderless(new PixelRect(0, 0, 2560, 1439), Monitor, Popup));
        Assert.False(GameWindowRules.IsBorderless(new PixelRect(1, 0, 2560, 1440), Monitor, Popup));
        Assert.False(GameWindowRules.IsBorderless(new PixelRect(0, 0, 1920, 1080), Monitor, Popup));
    }

    [Fact]
    public void EmptyRectangles_AreNotBorderless()
    {
        Assert.False(GameWindowRules.IsBorderless(default, default, Popup));
    }

    [Fact]
    public void Decide_GivesTheThreeStates()
    {
        Assert.Equal(GameWindowState.GameNotActive, GameWindowRules.Decide(false, Monitor, Monitor, Popup));
        Assert.Equal(GameWindowState.Ready, GameWindowRules.Decide(true, Monitor, Monitor, Popup));
        Assert.Equal(GameWindowState.NotBorderless, GameWindowRules.Decide(true, new PixelRect(100, 100, 1600, 900), Monitor, Popup | 0x00C00000u));
    }

    [Fact]
    public void BorderlessNotice_IsShownOncePerRun()
    {
        var tracker = new BorderlessNoticeTracker();

        Assert.True(tracker.ShouldNotify());
        tracker.MarkShown();
        Assert.False(tracker.ShouldNotify());
        Assert.True(tracker.WasShown);
    }

    [Fact]
    public void BorderlessNotice_NotUsedUpUntilMarkedShown()
    {
        var tracker = new BorderlessNoticeTracker();

        Assert.True(tracker.ShouldNotify());
        Assert.True(tracker.ShouldNotify()); // asking does not use it up: it may have been skipped because a question was open
        Assert.False(tracker.WasShown);
    }

    [Fact]
    public void BorderlessNotice_AlreadyNotBorderlessAtTheFirstCheck_IsShown()
    {
        var tracker = new BorderlessNoticeTracker();

        // IRIS was started while the game was already in front and not borderless: no transition happened, the very first check says NotBorderless
        Assert.True(tracker.ShouldShow(GameWindowState.NotBorderless, overlayDisabled: false, otherPopUpOpen: false));
    }

    [Fact]
    public void BorderlessNotice_SkippedCheckIsAskedAgainOnTheNextCheck()
    {
        var tracker = new BorderlessNoticeTracker();

        // another pop-up is open, then the overlay is disabled: nothing is used up, the state itself never changes
        Assert.False(tracker.ShouldShow(GameWindowState.NotBorderless, overlayDisabled: false, otherPopUpOpen: true));
        Assert.False(tracker.ShouldShow(GameWindowState.NotBorderless, overlayDisabled: true, otherPopUpOpen: false));
        Assert.True(tracker.ShouldShow(GameWindowState.NotBorderless, overlayDisabled: false, otherPopUpOpen: false));

        tracker.MarkShown();
        Assert.False(tracker.ShouldShow(GameWindowState.NotBorderless, overlayDisabled: false, otherPopUpOpen: false));
    }

    [Theory]
    [InlineData(GameWindowState.GameNotActive)]
    [InlineData(GameWindowState.Ready)]
    public void BorderlessNotice_OnlyForNotBorderless(GameWindowState state)
    {
        Assert.False(new BorderlessNoticeTracker().ShouldShow(state, overlayDisabled: false, otherPopUpOpen: false));
    }

    [Fact]
    public void BorderlessNotice_TextNamesTheIrisOverlay()
    {
        Assert.Equal(
            "Star Citizen is not in borderless windowed mode, so the IRIS overlay is turned off. Switch to Borderless in the game's graphics settings.",
            BorderlessNoticeTracker.Text);
    }
}

public class GameWindowModeTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);
    private static readonly PixelRect Smaller = new(100, 100, 1600, 900);
    private const uint PopupStyle = 0x80000000;   // WS_POPUP: no caption, no frame
    private const uint FramedStyle = PopupStyle | 0x00C00000u | 0x00040000u; // caption + resizable frame

    [Theory]
    [InlineData(WindowMode.Borderless, true, PopupStyle, GameWindowState.Ready)]
    [InlineData(WindowMode.Borderless, true, FramedStyle, GameWindowState.Ready)]      // the setting says borderless: the style is tolerated
    [InlineData(WindowMode.Borderless, false, PopupStyle, GameWindowState.NotBorderless)] // ...but the window must fill the monitor
    [InlineData(WindowMode.Borderless, false, FramedStyle, GameWindowState.NotBorderless)]
    [InlineData(WindowMode.Fullscreen, true, PopupStyle, GameWindowState.NotBorderless)] // exclusive fullscreen looks like borderless to the style check
    [InlineData(WindowMode.Fullscreen, true, FramedStyle, GameWindowState.NotBorderless)]
    [InlineData(WindowMode.Fullscreen, false, PopupStyle, GameWindowState.NotBorderless)]
    [InlineData(WindowMode.Windowed, true, PopupStyle, GameWindowState.NotBorderless)]
    [InlineData(WindowMode.Windowed, false, FramedStyle, GameWindowState.NotBorderless)]
    [InlineData(null, true, PopupStyle, GameWindowState.Ready)]                     // unknown: the window style check decides
    [InlineData(null, true, FramedStyle, GameWindowState.NotBorderless)]
    [InlineData(null, false, PopupStyle, GameWindowState.NotBorderless)]
    [InlineData(null, false, FramedStyle, GameWindowState.NotBorderless)]
    public void Decide_UsesTheGameMode_WhenKnown(WindowMode? mode, bool fillsMonitor, uint style, GameWindowState expected)
    {
        var window = fillsMonitor ? Monitor : Smaller;

        Assert.Equal(expected, GameWindowRules.Decide(true, window, Monitor, style, mode));
    }

    [Theory]
    [InlineData(WindowMode.Borderless)]
    [InlineData(WindowMode.Fullscreen)]
    [InlineData(WindowMode.Windowed)]
    [InlineData(null)]
    public void Decide_GameNotInFront_IsAlwaysNotActive(WindowMode? mode)
    {
        Assert.Equal(GameWindowState.GameNotActive, GameWindowRules.Decide(false, Monitor, Monitor, PopupStyle, mode));
    }

    [Fact]
    public void Decide_BorderlessWithEmptyRectangles_IsNotReady()
    {
        Assert.Equal(GameWindowState.NotBorderless, GameWindowRules.Decide(true, default, default, PopupStyle, WindowMode.Borderless));
    }

    [Fact]
    public void NoticeText_NamesTheActualMode()
    {
        Assert.Equal(
            "Star Citizen is in Fullscreen mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings.",
            BorderlessNoticeTracker.TextFor(WindowMode.Fullscreen));
        Assert.Equal(
            "Star Citizen is in Windowed mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings.",
            BorderlessNoticeTracker.TextFor(WindowMode.Windowed));
        Assert.Equal(BorderlessNoticeTracker.Text, BorderlessNoticeTracker.TextFor(null));
        Assert.Equal(BorderlessNoticeTracker.Text, BorderlessNoticeTracker.TextFor(WindowMode.Borderless));
    }
}

public class StarCitizenDisplaySettingsTests
{
    private static readonly string Install = Path.Combine(Path.GetTempPath(), "StarCitizen", "LIVE");
    private static readonly string Exe = Path.Combine(Install, "Bin64", "StarCitizen.exe");
    private static readonly string ClientDir = Path.Combine(Install, "user", "client");
    private static string Attributes(string profile) => Path.Combine(ClientDir, profile, "Profiles", "default", "attributes.xml");

    private static IEnumerable<(string path, DateTime modifiedUtc)> None(string dir) => Array.Empty<(string, DateTime)>();

    [Fact]
    public void PreferredProfileZero_IsUsedWhenItExists()
    {
        var result = StarCitizenDisplaySettings.AttributesPathFor(
            Exe,
            path => path == Attributes("0"),
            _ => throw new InvalidOperationException("candidates must not be searched when client\\0 has the file"));

        Assert.Equal(Attributes("0"), result);
    }

    [Fact]
    public void WithoutProfileZero_TheMostRecentlyModifiedCandidateWins()
    {
        var older = (Attributes("1"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = (Attributes("7"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = (Attributes("3"), new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        string? searched = null;

        var result = StarCitizenDisplaySettings.AttributesPathFor(
            Exe,
            _ => false,
            dir =>
            {
                searched = dir;
                return new[] { older, newest, middle };
            });

        Assert.Equal(Attributes("7"), result);
        Assert.Equal(ClientDir, searched);
    }

    [Fact]
    public void NoFileAnywhere_GivesNull()
    {
        Assert.Null(StarCitizenDisplaySettings.AttributesPathFor(Exe, _ => false, None));
    }

    [Fact]
    public void SideBySideInstalls_EachUsesItsOwnFolder()
    {
        var ptuExe = Path.Combine(Path.GetTempPath(), "StarCitizen", "PTU", "Bin64", "StarCitizen.exe");
        var ptuFile = Path.Combine(Path.GetTempPath(), "StarCitizen", "PTU", "user", "client", "0", "Profiles", "default", "attributes.xml");
        Func<string, bool> both = path => path == Attributes("0") || path == ptuFile;

        Assert.Equal(Attributes("0"), StarCitizenDisplaySettings.AttributesPathFor(Exe, both, None));
        Assert.Equal(ptuFile, StarCitizenDisplaySettings.AttributesPathFor(ptuExe, both, None));
    }

    [Fact]
    public void BinFolderNameIsMatchedWithoutRegardToCase()
    {
        var lower = Path.Combine(Install, "bin64", "StarCitizen.exe");

        Assert.Equal(Attributes("0"), StarCitizenDisplaySettings.AttributesPathFor(lower, path => path == Attributes("0"), None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("StarCitizen.exe")]
    public void UnusableExePath_GivesNull(string exe)
    {
        Assert.Null(StarCitizenDisplaySettings.AttributesPathFor(exe, _ => true, _ => new[] { ("x", DateTime.UtcNow) }));
    }

    [Fact]
    public void ExeNotInBin64_GivesNull()
    {
        var other = Path.Combine(Install, "Elsewhere", "StarCitizen.exe");

        Assert.Null(StarCitizenDisplaySettings.AttributesPathFor(other, _ => true, _ => new[] { ("x", DateTime.UtcNow) }));
    }

    [Theory]
    [InlineData("0", WindowMode.Windowed)]
    [InlineData("1", WindowMode.Borderless)]
    [InlineData("2", WindowMode.Fullscreen)]
    [InlineData(" 2 ", WindowMode.Fullscreen)]
    [InlineData("+1", WindowMode.Borderless)]
    public void KnownValues_AreMapped(string value, WindowMode expected)
    {
        var xml = $"<Attributes><Attr name=\"WindowMode\" value=\"{value}\"/></Attributes>";

        Assert.Equal(expected, StarCitizenDisplaySettings.ParseWindowMode(xml));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("100")]
    [InlineData("1.0")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999")]
    public void UnknownOrNonIntegerValues_GiveNull(string value)
    {
        var xml = $"<Attributes><Attr name=\"WindowMode\" value=\"{value}\"/></Attributes>";

        Assert.Null(StarCitizenDisplaySettings.ParseWindowMode(xml));
    }

    [Fact]
    public void RealisticFile_IsParsed()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<!-- settings -->\r\n<Attributes>\r\n"
            + " <Attr name=\"Width\" value=\"2560\"/>\r\n <Attr name=\"WindowMode\" value=\"2\"/>\r\n <Attr name=\"Height\" value=\"1440\"/>\r\n</Attributes>\r\n";

        Assert.Equal(WindowMode.Fullscreen, StarCitizenDisplaySettings.ParseWindowMode(xml));
    }

    [Fact]
    public void OtherAttributesNamedLikeIt_AreNotConfused()
    {
        // no real WindowMode line in a valid file: Windowed (the game removes the line for Windowed)
        Assert.Equal(WindowMode.Windowed,
            StarCitizenDisplaySettings.ParseWindowMode("<Attributes><Attr name=\"WindowModeX\" value=\"1\"/><Attr name=\"windowmode\" value=\"1\"/></Attributes>"));
    }

    [Fact]
    public void ValidFileWithoutWindowModeLine_IsWindowed()
    {
        Assert.Equal(WindowMode.Windowed, StarCitizenDisplaySettings.ParseWindowMode("<Attributes><Attr name=\"Width\" value=\"1\"/></Attributes>"));
        Assert.Equal(WindowMode.Windowed, StarCitizenDisplaySettings.ParseWindowMode("<Attributes/>"));
    }

    [Fact]
    public void WindowModeLineWithoutValue_GivesNull()
    {
        Assert.Null(StarCitizenDisplaySettings.ParseWindowMode("<Attributes><Attr name=\"WindowMode\"/></Attributes>"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<Attributes><Attr name=\"WindowMode\" value=\"1\"")] // cut off
    [InlineData("<Attributes><Attr name=\"Width\" value=\"1\"></Attributes>")] // broken before the element
    public void NotXml_GivesNull(string? text)
    {
        Assert.Null(StarCitizenDisplaySettings.ParseWindowMode(text));
    }

    [Fact]
    public void DtdIsRefused_WithoutResolvingAnything()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE Attributes [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>"
            + "<Attributes><Attr name=\"WindowMode\" value=\"1\"/></Attributes>";

        Assert.Null(StarCitizenDisplaySettings.ParseWindowMode(xml));
    }
}

public class OverlayPaletteTests
{
    [Fact]
    public void EightColors_AreDistinct_AndNotWhiteOrBlack()
    {
        var all = Enum.GetValues<OverlayColor>().Select(OverlayPalette.Rgb).ToList();

        Assert.Equal(8, all.Count);
        Assert.Equal(8, all.Distinct().Count());
        Assert.DoesNotContain((0xFF, 0xFF, 0xFF), all.Select(c => ((int)c.R, (int)c.G, (int)c.B)));
        Assert.All(all, c => Assert.True(Math.Max(c.R, Math.Max(c.G, c.B)) >= 0xE0, "vivid: at least one strong channel"));
    }
}
