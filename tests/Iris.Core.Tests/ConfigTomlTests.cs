using Iris.Core.Config;
using Xunit;

namespace Iris.Core.Tests;

public class ConfigTomlTests
{
    private static SocialConfig Sample()
    {
        var c = new SocialConfig { Name = "Squad \"Alpha\" \\ test" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));
        c.Players.Add(new PlayerEntry("MARA_QUILLON", OverlayColor.Green));
        c.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        c.Orgs.Add(new OrgEntry("HEKATE", 3, OverlayColor.Blue));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 2, OverlayColor.Orange));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 5, OverlayColor.Magenta));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoOrg, null, 9, OverlayColor.Yellow));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Purple));
        return c;
    }

    private const string Header = "format = \"sc-social-overlay\"\nformat_version = 1\n";

    private static string NoMatchBlock(string body) => "\n[[org_rules]]\nkind = \"no_match\"\n" + body;

    [Fact]
    public void WriteThenRead_RoundTripsEverything()
    {
        var text = SocialConfigToml.Write(Sample());
        var r = SocialConfigToml.Read(text);

        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal("Squad \"Alpha\" \\ test", r.Config!.Name);
        Assert.Equal(Sample().Players, r.Config.Players);
        Assert.Equal(Sample().Orgs, r.Config.Orgs);
        Assert.Equal(Sample().OrgRules, r.Config.OrgRules); // including the No Match rule (Purple), last
    }

    [Fact]
    public void NoMatch_IsWrittenAsTheLastRule_WithPriorityTen_AndNoValue()
    {
        var text = SocialConfigToml.Write(Sample());

        var last = text.LastIndexOf("[[org_rules]]", StringComparison.Ordinal);
        var tail = text.Substring(last);
        Assert.Equal("[[org_rules]]\nkind = \"no_match\"\npriority = 10\ncolor = \"Purple\"\n", tail.Replace("\r\n", "\n"));
        Assert.Equal(1, text.Split("no_match").Length - 1);
    }

    [Fact]
    public void Write_ConfigWithoutNoMatch_WritesTheDefault_Blue()
    {
        var c = new SocialConfig();
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 2, OverlayColor.Red));

        var r = SocialConfigToml.Read(SocialConfigToml.Write(c));

        Assert.True(r.IsValid);
        Assert.Empty(r.Warnings);
        Assert.Equal(AttributeKind.Redacted, r.Config!.OrgRules[0].Kind);
        Assert.Equal(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Blue), r.Config.OrgRules[^1]);
        Assert.Equal(2, r.Config.OrgRules.Count);
    }

    [Fact]
    public void Write_NoMatchStoredFirstOrWithOtherPriority_IsWrittenLastWithPriorityTen()
    {
        var c = new SocialConfig();
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, "ignored", 3, OverlayColor.Cyan));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 2, OverlayColor.Red));

        var r = SocialConfigToml.Read(SocialConfigToml.Write(c));

        Assert.True(r.IsValid);
        Assert.Empty(r.Warnings);
        Assert.Equal(AttributeKind.Redacted, r.Config!.OrgRules[0].Kind);
        Assert.Equal(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Cyan), r.Config.OrgRules[1]);
    }

    [Fact]
    public void Read_FileWithoutNoMatch_AddsTheDefaultSilently()
    {
        var r = SocialConfigToml.Read(Header);

        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(new[] { new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Blue) }, r.Config!.OrgRules);
    }

    [Fact]
    public void Read_NoMatchInTheMiddleOfTheFile_MovesToTheEnd_Silently()
    {
        var text = Header
            + NoMatchBlock("priority = 10\ncolor = \"Green\"\n")
            + "\n[[org_rules]]\nkind = \"redacted\"\npriority = 4\ncolor = \"Red\"\n";

        var r = SocialConfigToml.Read(text);

        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(new[] { AttributeKind.Redacted, AttributeKind.NoMatch }, r.Config!.OrgRules.Select(x => x.Kind));
        Assert.Equal(OverlayColor.Green, r.Config.OrgRules[1].Color);
    }

    [Fact]
    public void Read_TwoNoMatchRules_KeepsTheFirst_WithAWarning()
    {
        var text = Header
            + NoMatchBlock("priority = 10\ncolor = \"Green\"\n")
            + NoMatchBlock("priority = 10\ncolor = \"Red\"\n");

        var r = SocialConfigToml.Read(text);

        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        var rule = Assert.Single(r.Config!.OrgRules);
        Assert.Equal(OverlayColor.Green, rule.Color);
        var warning = Assert.Single(r.Warnings);
        Assert.StartsWith("org_rules[2]", warning);
        Assert.Contains("only one", warning);
    }

    [Theory]
    [InlineData("priority = 5\ncolor = \"Red\"\n")]
    [InlineData("priority = 1\ncolor = \"Red\"\n")]
    [InlineData("priority = 11\ncolor = \"Red\"\n")]
    [InlineData("priority = \"ten\"\ncolor = \"Red\"\n")]
    public void Read_NoMatchWithAnotherPriority_UsesTenWithAWarning(string body)
    {
        var r = SocialConfigToml.Read(Header + NoMatchBlock(body));

        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        Assert.Equal(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Red), Assert.Single(r.Config!.OrgRules));
        var warning = Assert.Single(r.Warnings);
        Assert.StartsWith("org_rules[1].priority", warning);
        Assert.Contains("10", warning);
    }

    [Fact]
    public void Read_NoMatchWithoutPriority_IsAccepted_AndAValueIsIgnoredWithAWarning()
    {
        var r = SocialConfigToml.Read(Header + NoMatchBlock("value = \"PMC\"\ncolor = \"Orange\"\n"));

        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        Assert.Equal(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Orange), Assert.Single(r.Config!.OrgRules));
        var warning = Assert.Single(r.Warnings);
        Assert.StartsWith("org_rules[1].value", warning);
    }

    [Fact]
    public void Read_NoMatchNeedsAValidColor()
    {
        var r = SocialConfigToml.Read(Header + NoMatchBlock("priority = 10\ncolor = \"White\"\n"));

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("org_rules[1].color"));
    }

    [Fact]
    public void KindText_NoMatch_IsNoUnderscoreMatch()
    {
        Assert.Equal("no_match", SocialConfigToml.KindToText(AttributeKind.NoMatch));
        Assert.Equal(AttributeKind.NoMatch, SocialConfigToml.TextToKind(" No_Match "));
    }

    [Fact]
    public void Read_UnknownKind_ErrorListsNoMatchAmongTheKinds()
    {
        var r = SocialConfigToml.Read(Header + "\n[[org_rules]]\nkind = \"weather\"\npriority = 1\ncolor = \"Red\"\n");

        Assert.Contains(r.Errors, e => e.StartsWith("org_rules[1].kind") && e.Contains("no_match"));
    }

    [Fact]
    public void NoMatchRule_Ensure_KeepsTheFirst_DropsTheRest_PutsItLast()
    {
        var c = new SocialConfig();
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 7, OverlayColor.Red));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 2, OverlayColor.Cyan));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Green));

        NoMatchRule.Ensure(c);

        Assert.Equal(2, c.OrgRules.Count);
        Assert.Equal(AttributeKind.Redacted, c.OrgRules[0].Kind);
        Assert.Equal(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Red), c.OrgRules[1]);
        Assert.Equal(1, c.UserRuleCount);
    }

    [Fact]
    public void NoMatchRule_Normalized_DoesNotChangeTheOriginal()
    {
        var c = new SocialConfig { Name = "X" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));

        var copy = NoMatchRule.Normalized(c);

        Assert.Empty(c.OrgRules);
        Assert.Equal("X", copy.Name);
        Assert.Single(copy.Players);
        Assert.Equal(NoMatchRule.CreateDefault(), Assert.Single(copy.OrgRules));
    }

    [Fact]
    public void Quote_EscapesSpecialCharacters()
    {
        Assert.Equal("\"a\\\"b\\\\c\\nd\\te\"", SocialConfigToml.Quote("a\"b\\c\nd\te"));
        Assert.Equal("\"\\u0001\"", SocialConfigToml.Quote("\u0001"));
        Assert.Equal("\"Ünïcödé ✓\"", SocialConfigToml.Quote("Ünïcödé ✓"));
    }

    [Fact]
    public void Read_RejectsFileThatIsNotASocialOverlayConfig()
    {
        var r = SocialConfigToml.Read("title = \"some other app\"\n");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("format:"));
    }

    [Fact]
    public void Read_ReportsTomlSyntaxErrors()
    {
        var r = SocialConfigToml.Read("format = \"sc-social-overlay\"\nformat_version = \n");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("TOML syntax"));
    }

    [Fact]
    public void Read_RejectsNewerFormatVersion()
    {
        var r = SocialConfigToml.Read("format = \"sc-social-overlay\"\nformat_version = 2\n");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("format_version"));
    }

    [Fact]
    public void Read_ValidatesRows()
    {
        const string text = """
            format = "sc-social-overlay"
            format_version = 1

            [[players]]
            handle = "bad name"
            color = "Cyan"

            [[players]]
            handle = "Nitebird"
            color = "White"

            [[players]]
            handle = "Ok_Name-1"
            color = "red"

            [[players]]
            handle = "ok_name-1"
            color = "Blue"

            [[orgs]]
            sid = "J0K3R5"
            priority = 10
            color = "Red"

            [[org_rules]]
            kind = "archetype"
            priority = 1
            color = "Red"

            [[org_rules]]
            kind = "weather"
            priority = 1
            color = "Red"
            """;
        var r = SocialConfigToml.Read(text);

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("players[1].handle") && e.Contains("not a valid handle"));
        Assert.Contains(r.Errors, e => e.StartsWith("players[2].color"));
        Assert.Contains(r.Errors, e => e.StartsWith("players[4].handle") && e.Contains("more than once"));
        Assert.Contains(r.Errors, e => e.StartsWith("orgs[1].priority"));
        Assert.Contains(r.Errors, e => e.StartsWith("org_rules[1].value"));
        Assert.Contains(r.Errors, e => e.StartsWith("org_rules[2].kind"));
        // colors are case-insensitive: "red" is accepted
        Assert.Contains(r.Config!.Players, p => p.Handle == "Ok_Name-1" && p.Color == OverlayColor.Red);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public void Read_PlayerHandleLengthMustBe4To25(int length, bool valid)
    {
        var text = "format = \"sc-social-overlay\"\nformat_version = 1\n\n[[players]]\nhandle = \""
                   + new string('a', length) + "\"\ncolor = \"Cyan\"\n";
        var r = SocialConfigToml.Read(text);

        Assert.Equal(valid, r.IsValid);
        if (!valid)
            Assert.Contains(r.Errors, e => e.StartsWith("players[1].handle") && e.Contains("4 to 25 characters"));
    }

    [Fact]
    public void Read_WarnsAboutUnknownKeysButStaysValid()
    {
        const string text = """
            format = "sc-social-overlay"
            format_version = 1
            future_setting = true

            [[players]]
            handle = "Nitebird"
            color = "Cyan"
            note = "added by a newer version"
            """;
        var r = SocialConfigToml.Read(text);
        Assert.True(r.IsValid, string.Join("\n", r.Errors));
        Assert.Equal(2, r.Warnings.Count);
    }
}
