using Astra.Core.Rsi;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// The parsers against REAL RSI pages saved on the developer's PC (run tools/Astra.CaptureFixtures first).
/// Without the saved files each test is skipped. Assertions are loose on purpose: orgs and member lists change over time.
/// </summary>
public class RsiFixtureTests
{
    private static PlayerOrgs ParsePlayer(string file, string handle)
    {
        var r = PlayerOrgsParser.Parse(FixtureFactAttribute.Read(file), handle);
        Assert.True(r.IsFound, $"{file}: {r}");
        return r.Data!;
    }

    [FixtureFact("player-Nitebird.html")]
    public void Nitebird_MainOrgHekate_AndAffiliatesInPageOrder()
    {
        var player = ParsePlayer("player-Nitebird.html", "Nitebird");

        Assert.Equal("Nitebird", player.Handle, ignoreCase: true);

        var main = Assert.Single(player.Orgs, o => o.IsMain);
        Assert.Equal("HEKATE", main.Sid);
        Assert.Equal("Organization", main.Archetype);
        Assert.Contains("Freelancing", main.ActivityList);
        Assert.Contains("Social", main.ActivityList);
        Assert.True(player.Orgs[0].IsMain, "the main org must come first");

        var affiliates = player.Orgs.Where(o => !o.IsMain).Select(o => o.Sid).ToList();
        Assert.Equal(new string?[] { "J0K3R5", "AVOCADO", "ASTROMERCS", "VNGD" }, affiliates);
        Assert.All(player.Orgs.Where(o => !o.IsMain), o => Assert.Null(o.Archetype));   // affiliates come without attributes
    }

    [FixtureFact("player-Bqnzai.html")]
    public void Bqnzai_HiddenMainOrg_AndGriefernetAffiliation()
    {
        var player = ParsePlayer("player-Bqnzai.html", "Bqnzai");

        var main = Assert.Single(player.Orgs, o => o.IsMain);
        Assert.True(main.IsRedacted);
        Assert.Contains(player.Orgs, o => !o.IsMain && o.Sid == "GRIEFERNET");
    }

    [FixtureFact("player-Love2Hate.html")]
    public void Love2Hate_MainPermafleet_Griefernet_AndOneHiddenAffiliation()
    {
        var player = ParsePlayer("player-Love2Hate.html", "Love2Hate");

        var main = Assert.Single(player.Orgs, o => o.IsMain);
        Assert.Equal("PERMAFLEET", main.Sid);
        Assert.Contains(player.Orgs, o => !o.IsMain && o.Sid == "GRIEFERNET");
        Assert.Single(player.Orgs, o => !o.IsMain && o.IsRedacted);
    }

    [FixtureFact("org-J0K3R5.html")]
    public void OrgPage_J0K3R5_LongNameAndTags()
    {
        var r = OrgPageParser.Parse(FixtureFactAttribute.Read("org-J0K3R5.html"), "J0K3R5");

        Assert.True(r.IsFound, r.ToString());
        var org = r.Data!;
        Assert.Equal("J0K3R5", org.Sid);
        Assert.Equal("Jokers Gambit", org.LongName);
        Assert.Equal("PMC", org.Archetype);
        Assert.Equal("Regular", org.Commitment);
        Assert.True(org.IsExclusive);
        Assert.Contains("Freelancing", org.Activities);
        Assert.True(org.MemberCount > 0, "member count should be read from the page");
    }

    [FixtureFact("members-J0K3R5-page1.json")]
    public void MembersApi_J0K3R5_Page1_HasVisibleMembers()
    {
        var r = OrgMembersParser.ParseResponse(FixtureFactAttribute.Read("members-J0K3R5-page1.json"));

        Assert.True(r.IsFound, r.ToString());
        var page = r.Data!;
        Assert.True(page.EntryCount > 0, "page 1 should list members");
        Assert.NotEmpty(page.Handles);
        Assert.All(page.Handles, h => Assert.True(RsiNames.IsValidHandle(h), h));
        Assert.True(page.Handles.Count <= page.EntryCount);
        Assert.True(page.EntryCount <= RsiClient.MembersPageSize);
    }

    [FixtureFact("player-nonexistent.status.txt")]
    public void NonExistentHandle_RecordedStatusIs404()
    {
        Assert.Equal("404", FixtureFactAttribute.Read("player-nonexistent.status.txt").Trim());
    }
}
