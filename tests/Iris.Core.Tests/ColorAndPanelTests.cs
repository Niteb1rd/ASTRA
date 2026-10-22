using Iris.Core.Config;
using Iris.Core.Social;
using Xunit;

namespace Iris.Core.Tests;

public class ColorAndPanelTests
{
    // Nitebird's real orgs (from RSI): main HEKATE, affiliates J0K3R5, AVOCADO, ASTROMERCS, VNGD
    private static readonly OrgMembership[] Nitebird =
    {
        new("HEKATE", true, "Organization", new[] { "Freelancing", "Social" }),
        new("J0K3R5", false, "PMC", new[] { "Freelancing" }),
        new("AVOCADO", false),
        new("ASTROMERCS", false),
        new("VNGD", false),
    };

    private static SocialConfig Config(Action<SocialConfig> f)
    {
        var c = new SocialConfig();
        f(c);
        return c;
    }

    [Fact]
    public void PlayerList_WinsOverEverything_CurlyBrackets()
    {
        var c = Config(c =>
        {
            c.Players.Add(new PlayerEntry("nitebird", OverlayColor.Cyan));      // case-insensitive
            c.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        });
        var m = ColorResolver.Resolve("Nitebird", Nitebird, c)!;
        Assert.Equal(MatchKind.PlayerList, m.Kind);
        Assert.Equal(OverlayColor.Cyan, m.Color);
        Assert.Equal("{", m.OpenBracket);
        Assert.Equal("Orgs: HEKATE*, J0K3R5, AVOCADO, etc.", PanelText.OrgLine(Nitebird, m));
    }

    [Fact]
    public void OrgList_LowestPriorityNumberWins_SquareBrackets()
    {
        var c = Config(c =>
        {
            c.Orgs.Add(new OrgEntry("HEKATE", 3, OverlayColor.Blue));
            c.Orgs.Add(new OrgEntry("J0K3R5", 1, OverlayColor.Red));
        });
        var m = ColorResolver.Resolve("Nitebird", Nitebird, c)!;
        Assert.Equal(MatchKind.OrgList, m.Kind);
        Assert.Equal(OverlayColor.Red, m.Color);
        Assert.Equal("[", m.OpenBracket);
        Assert.Equal("Orgs: J0K3R5, HEKATE*, AVOCADO, etc.", PanelText.OrgLine(Nitebird, m));
    }

    [Fact]
    public void OrgList_TieGoesToFirstListedOrg()
    {
        var c = Config(c =>
        {
            c.Orgs.Add(new OrgEntry("VNGD", 2, OverlayColor.Green));
            c.Orgs.Add(new OrgEntry("J0K3R5", 2, OverlayColor.Red));
        });
        var m = ColorResolver.Resolve("Nitebird", Nitebird, c)!;
        Assert.Equal(OverlayColor.Green, m.Color);
        Assert.Equal("VNGD", m.DecidingOrgSid);
    }

    [Fact]
    public void AttributeRule_MatchesAnyOrg_AngleBrackets_ShowsAttribute()
    {
        var c = Config(c => c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "pmc", 1, OverlayColor.Orange)));
        var m = ColorResolver.Resolve("Nitebird", Nitebird, c)!;
        Assert.Equal(MatchKind.OrgAttribute, m.Kind);
        Assert.Equal("<", m.OpenBracket);
        Assert.Equal("J0K3R5", m.DecidingOrgSid);
        Assert.Equal("Orgs: J0K3R5 (pmc), HEKATE*, etc.", PanelText.OrgLine(Nitebird, m));
    }

    [Fact]
    public void AttributeRules_PriorityThenListOrder()
    {
        var c = Config(c =>
        {
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Activity, "Social", 4, OverlayColor.Purple));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Activity, "Freelancing", 4, OverlayColor.Yellow));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 6, OverlayColor.Orange));
        });
        var m = ColorResolver.Resolve("Nitebird", Nitebird, c)!;
        Assert.Equal(OverlayColor.Purple, m.Color);       // tie at 4 -> first listed
        Assert.Equal("HEKATE", m.DecidingOrgSid);
    }

    [Fact]
    public void OrgListBeatsAttributeRules_EvenWithWorsePriority()
    {
        var c = Config(c =>
        {
            c.Orgs.Add(new OrgEntry("VNGD", 9, OverlayColor.Green));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 1, OverlayColor.Orange));
        });
        Assert.Equal(MatchKind.OrgList, ColorResolver.Resolve("Nitebird", Nitebird, c)!.Kind);
    }

    [Fact]
    public void RedactedAndNoOrgRules()
    {
        var c = Config(c =>
        {
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Redacted, null, 2, OverlayColor.Magenta));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoOrg, null, 3, OverlayColor.Yellow));
        });

        // Bqnzai: hidden main org + GRIEFERNET
        var bq = new OrgMembership[] { new(null, true), new("GRIEFERNET", false) };
        var m = ColorResolver.Resolve("Bqnzai", bq, c)!;
        Assert.Equal(OverlayColor.Magenta, m.Color);
        Assert.Equal("Orgs: REDACTED*, GRIEFERNET", PanelText.OrgLine(bq, m));

        // Love2Hate: hidden affiliate is moved first for a REDACTED match
        var lh = new OrgMembership[] { new("PERMAFLEET", true), new("GRIEFERNET", false), new(null, false) };
        Assert.Equal("Orgs: REDACTED, PERMAFLEET*, GRIEFERNET", PanelText.OrgLine(lh, ColorResolver.Resolve("Love2Hate", lh, c)));

        // no orgs at all
        var none = Array.Empty<OrgMembership>();
        var m2 = ColorResolver.Resolve("Loner", none, c)!;
        Assert.Equal(OverlayColor.Yellow, m2.Color);
        Assert.Equal("Orgs: none found", PanelText.OrgLine(none, m2));
    }

    [Fact]
    public void NoMatch_NothingMatches_GetsTheNoMatchColor_AngleBrackets_PanelStillInRsiOrder()
    {
        var c = Config(c => c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Magenta)));
        var m = ColorResolver.Resolve("Nitebird", Nitebird, c);

        Assert.Equal(MatchKind.OrgAttribute, m.Kind);
        Assert.Equal(OverlayColor.Magenta, m.Color);
        Assert.Equal("<", m.OpenBracket);
        Assert.Equal(">", m.CloseBracket);
        Assert.Null(m.DecidingOrgSid);
        Assert.Null(m.DecidingAttribute);
        // the orgs line as usual: RSI order, main first, no org marked as the reason
        Assert.Equal("Orgs: HEKATE*, J0K3R5, AVOCADO, etc.", PanelText.OrgLine(Nitebird, m));
    }

    [Fact]
    public void NoMatch_ConfigWithoutTheRule_FallsBackToTheDefaultBlue_StillBracketed()
    {
        var m = ColorResolver.Resolve("Nitebird", Nitebird, new SocialConfig());

        Assert.Equal(MatchKind.OrgAttribute, m.Kind);
        Assert.Equal(OverlayColor.Blue, m.Color);
        Assert.Equal("<", m.OpenBracket);
    }

    [Fact]
    public void NoMatch_PlayerWithNoOrgs_IsBracketedToo_AndPanelSaysNoneFound()
    {
        var none = Array.Empty<OrgMembership>();
        var c = Config(c => c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Green)));

        var m = ColorResolver.Resolve("Loner", none, c);

        Assert.Equal(OverlayColor.Green, m.Color);
        Assert.Equal(MatchKind.OrgAttribute, m.Kind);
        Assert.Equal("Orgs: none found", PanelText.OrgLine(none, m));
    }

    [Fact]
    public void NoMatch_NeverBeatsARealMatch()
    {
        var c = Config(c =>
        {
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Green));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 9, OverlayColor.Orange)); // worst real priority, listed after it
        });

        var m = ColorResolver.Resolve("Nitebird", Nitebird, c);

        Assert.Equal(OverlayColor.Orange, m.Color);
        Assert.Equal("J0K3R5", m.DecidingOrgSid);
        Assert.Equal("PMC", m.DecidingAttribute);
    }

    [Fact]
    public void NoMatch_PlayerListAndOrgListStillWin()
    {
        var c = Config(c =>
        {
            c.Players.Add(new PlayerEntry("Nitebird", OverlayColor.Cyan));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Green));
        });
        Assert.Equal(MatchKind.PlayerList, ColorResolver.Resolve("Nitebird", Nitebird, c).Kind);

        var c2 = Config(c =>
        {
            c.Orgs.Add(new OrgEntry("VNGD", 9, OverlayColor.Red));
            c.OrgRules.Add(new OrgAttributeRule(AttributeKind.NoMatch, null, 10, OverlayColor.Green));
        });
        Assert.Equal(MatchKind.OrgList, ColorResolver.Resolve("Nitebird", Nitebird, c2).Kind);
    }

    [Fact]
    public void NoMatch_ConfigRead_FromAFileWithoutTheRule_UsesBlue()
    {
        var read = SocialConfigToml.Read("format = \"sc-social-overlay\"\nformat_version = 1\n");

        var m = ColorResolver.Resolve("Nitebird", Nitebird, read.Config!);

        Assert.Equal(OverlayColor.Blue, m.Color);
        Assert.Equal("<", m.OpenBracket);
    }

    [Fact]
    public void OrgLine_NeverExceeds40Characters()
    {
        var many = Enumerable.Range(0, 12).Select(i => new OrgMembership($"ORG{i:00}LONGSID", i == 0)).ToArray();
        var line = PanelText.OrgLine(many, null);
        Assert.True(line.Length <= 40, line);
        Assert.EndsWith(", etc.", line);

        var huge = new[] { new OrgMembership("ABCDEFGHIJKLMNOPQRST", true, "PMC") };
        var c = Config(c => c.OrgRules.Add(new OrgAttributeRule(AttributeKind.Archetype, "PMC", 1, OverlayColor.Red)));
        var hugeLine = PanelText.OrgLine(huge, ColorResolver.Resolve("x", huge, c));
        Assert.True(hugeLine.Length <= 40, hugeLine);
    }
}
