using Astra.Core.Config;
using Xunit;

namespace Astra.Core.Tests;

public class ConfigTomlTests
{
    private static SocialConfig Sample()
    {
        var c = new SocialConfig { Name = "Squad \"Alpha\" \\ test" };
        c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));
        c.Players.Add(new PlayerEntry("TORIN_DEMPSEY", OverlayColor.Green));
        c.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        c.Orgs.Add(new OrgEntry("HEKATE", 3, OverlayColor.Blue));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 2, OverlayColor.Orange));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 5, OverlayColor.Magenta));
        c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoOrg, null, 9, OverlayColor.Yellow));
        return c;
    }

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
        Assert.Equal(Sample().OrgRules, r.Config.OrgRules);
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
