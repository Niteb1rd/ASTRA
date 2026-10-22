using Iris.Core.Rsi;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The parsers against REAL RSI pages saved on the developer's PC (run tools/Iris.CaptureFixtures first).
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

    // ----- extra pages captured with:  --orgs-of Nitebird --players Gold,...  (checked 2026-10-21) -----

    [FixtureFact("player-Gold.html")]
    public void PlayerWithNoOrgs_IsFound_WithAnEmptyList()
    {
        // RSI shows "NO ORG MEMBERSHIP FOUND IN PUBLIC RECORDS": a real "no org", not an unreadable page
        var player = ParsePlayer("player-Gold.html", "Gold");
        Assert.Empty(player.Orgs);
    }

    [FixtureFact("player-FakeName1.status.txt")]
    public void MadeUpHandle_RecordedStatusIs404()
    {
        Assert.Equal("404", FixtureFactAttribute.Read("player-FakeName1.status.txt").Trim());
    }

    [FixtureFact("org-AVOCADO.html")]
    public void OrgPage_AVOCADO_FaithHardcore()
    {
        var org = ParseOrg("org-AVOCADO.html", "AVOCADO");
        Assert.Equal("Evocati - NDA", org.LongName);
        Assert.Equal("Faith", org.Archetype);
        Assert.Equal("Hardcore", org.Commitment);
        Assert.Contains("Exploration", org.Activities);
    }

    [FixtureFact("org-ASTROMERCS.html")]
    public void OrgPage_ASTROMERCS_PmcWithTwoWordActivity()
    {
        var org = ParseOrg("org-ASTROMERCS.html", "ASTROMERCS");
        Assert.Equal("Astro Mercenaries", org.LongName);
        Assert.Equal("PMC", org.Archetype);
        Assert.Contains("Bounty Hunting", org.Activities);
    }

    [FixtureFact("org-HEKATE.html")]
    public void OrgPage_HEKATE_Organization()
    {
        var org = ParseOrg("org-HEKATE.html", "HEKATE");
        Assert.Equal("Organization", org.Archetype);
        Assert.Equal("Regular", org.Commitment);
    }

    [FixtureFact("members-J0K3R5-page1.json")]
    public void MembersApi_RedactedMembers_AreLeftOut()
    {
        // J0K3R5 page 1 (2026-10-21): 27 visible + 5 redacted (org-visibility-R)
        AssertOnlyVisibleMembersRead("members-J0K3R5-page1.json");
    }

    [FixtureFact("members-AVOCADO-page1.json")]
    public void MembersApi_HiddenMembers_AreLeftOut()
    {
        // AVOCADO page 1 (2026-10-21): 30 visible + 2 hidden (org-visibility-H)
        AssertOnlyVisibleMembersRead("members-AVOCADO-page1.json");
    }

    private static OrgInfo ParseOrg(string file, string sid)
    {
        var r = OrgPageParser.Parse(FixtureFactAttribute.Read(file), sid);
        Assert.True(r.IsFound, $"{file}: {r}");
        Assert.Equal(sid, r.Data!.Sid);
        return r.Data;
    }

    /// <summary>The handles read must be exactly the members RSI marks visible ("org-visibility-V").</summary>
    private static void AssertOnlyVisibleMembersRead(string file)
    {
        var json = FixtureFactAttribute.Read(file);
        var r = OrgMembersParser.ParseResponse(json);
        Assert.True(r.IsFound, $"{file}: {r}");

        int visible = CountOf(json, "org-visibility-V");
        Assert.True(visible > 0, "the page should list visible members");
        Assert.Equal(visible, r.Data!.Handles.Count);
        Assert.True(r.Data.Handles.Count <= r.Data.EntryCount);
    }

    private static int CountOf(string text, string word)
    {
        int count = 0;
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [FixtureFact("player-nonexistent.status.txt")]
    public void NonExistentHandle_RecordedStatusIs404()
    {
        Assert.Equal("404", FixtureFactAttribute.Read("player-nonexistent.status.txt").Trim());
    }
}
