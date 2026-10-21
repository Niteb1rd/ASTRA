using System.Text.Json;
using Astra.Core.Rsi;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// Parser tests on small hand-written pages. They only prove the parsers do what their constants say;
/// whether RSI's real pages look like this is checked by the fixture tests (RsiFixtureTests).
/// </summary>
public class SyntheticParserTests
{
    private const string PlayerPage = """
        <html><body>
        <div class="profile-content">
          <div class="profile">
            <p class="entry"><span class="label">Handle name</span><strong class="value">Nitebird</strong></p>
          </div>
          <div class="box-content org main visibility-V">
            <div class="heading"><h3>Main organization</h3></div>
            <div class="inner">
              <a href="/orgs/HEKATE">Hekate Syndicate Trading</a>
              <p class="entry"><span class="label">Spectrum Identification (SID)</span><strong class="value">HEKATE</strong></p>
              <p class="entry"><span class="label">Organization rank</span><strong class="value">Recruit</strong></p>
              <p class="entry"><span class="label">Archetype</span><strong class="value">Organization</strong></p>
              <ul class="focus"><li><img alt="Freelancing" src="a.png"></li><li><img alt="Social" src="b.png"></li></ul>
            </div>
          </div>
          <div class="box-content org affiliation visibility-V">
            <div class="heading"><h3>Affiliation</h3></div>
            <div class="inner">
              <a href="/orgs/J0K3R5">Jokers Gambit</a>
              <p class="entry"><span class="label">Spectrum Identification (SID)</span><strong class="value">J0K3R5</strong></p>
              <p class="entry"><span class="label">Organization rank</span><strong class="value">Member</strong></p>
            </div>
          </div>
          <div class="box-content org affiliation visibility-R">
            <div class="heading"><h3>Affiliation</h3></div>
            <div class="empty"><span class="title">REDACTED</span></div>
          </div>
          <div class="box-content org affiliation visibility-V">
            <div class="heading"><h3>Affiliation</h3></div>
            <div class="inner"><a href="/orgs/VNGD">Vanguard</a></div>
          </div>
        </div>
        </body></html>
        """;

    [Fact]
    public void PlayerPage_ReadsMainAffiliatesAndHiddenOrgsInPageOrder()
    {
        var r = PlayerOrgsParser.Parse(PlayerPage, "nitebird");

        Assert.True(r.IsFound, r.ToString());
        var data = r.Data!;
        Assert.Equal("Nitebird", data.Handle);   // as RSI shows it, not as requested
        Assert.Equal(4, data.Orgs.Count);

        Assert.True(data.Orgs[0].IsMain);
        Assert.Equal("HEKATE", data.Orgs[0].Sid);
        Assert.Equal("Organization", data.Orgs[0].Archetype);
        Assert.Contains("Freelancing", data.Orgs[0].ActivityList);
        Assert.Contains("Social", data.Orgs[0].ActivityList);
        Assert.DoesNotContain("Trading", data.Orgs[0].ActivityList);   // part of a link text (the org's name): ignored

        Assert.Equal("J0K3R5", data.Orgs[1].Sid);
        Assert.False(data.Orgs[1].IsMain);
        Assert.Null(data.Orgs[1].Archetype);

        Assert.True(data.Orgs[2].IsRedacted);
        Assert.False(data.Orgs[2].IsMain);

        Assert.Equal("VNGD", data.Orgs[3].Sid);   // SID taken from the link when there is no SID row
    }

    [Fact]
    public void PlayerPage_HiddenMainOrg_IsMainWithNullSid()
    {
        const string html = """
            <html><body><div class="profile-content">
            <div class="box-content org main visibility-R"><div class="heading"><h3>Main organization</h3></div><div class="empty"><span class="title">REDACTED</span></div></div>
            <div class="box-content org affiliation"><div class="heading"><h3>Affiliation</h3></div><div class="inner"><a href="/orgs/GRIEFERNET">x</a></div></div>
            </div></body></html>
            """;

        var r = PlayerOrgsParser.Parse(html, "Bqnzai");

        Assert.True(r.IsFound, r.ToString());
        Assert.Equal("Bqnzai", r.Data!.Handle);   // no handle row: falls back to the requested one
        Assert.True(r.Data.Orgs[0].IsMain);
        Assert.True(r.Data.Orgs[0].IsRedacted);
        Assert.Equal("GRIEFERNET", r.Data.Orgs[1].Sid);
    }

    [Fact]
    public void PlayerPage_BlockSaying_NoOrg_IsNotAnOrg()
    {
        const string html = """
            <html><body><div class="profile-content">
            <div class="box-content org main"><div class="heading"><h3>Main organization</h3></div><div class="empty">NO MAIN ORG FOUND IN PUBLIC RECORDS</div></div>
            </div></body></html>
            """;

        var r = PlayerOrgsParser.Parse(html, "Loner");

        Assert.True(r.IsFound, r.ToString());
        Assert.Empty(r.Data!.Orgs);
    }

    [Fact]
    public void PlayerPage_ThatIsNotAProfile_Fails_NeverNotFound()
    {
        var r = PlayerOrgsParser.Parse("<html><body><h1>Maintenance</h1></body></html>", "Anyone");

        Assert.Equal(RsiStatus.Failed, r.Status);
        Assert.Contains("layout", r.Reason);
    }

    [Fact]
    public void PlayerPage_WithHeadingsButUnknownBlocks_Fails_InsteadOfClaimingNoOrgs()
    {
        var r = PlayerOrgsParser.Parse(
            "<html><body><div class=\"profile\"></div><section><h3>Main organization</h3></section></body></html>", "Anyone");

        Assert.Equal(RsiStatus.Failed, r.Status);
    }

    private const string OrgPage = """
        <html><body>
        <nav><a>Community</a></nav>
        <div id="organization">
          <h1>Jokers Gambit / <span class="symbol">J0K3R5</span></h1>
          <div class="logo"><span class="count">1,104 members</span></div>
          <ul class="tags"><li class="model">PMC</li><li class="commitment">Regular</li><li class="exclusive">Exclusive</li></ul>
          <div class="focus"><div class="primary"><img alt="Freelancing"></div><div class="secondary"><img alt=""></div></div>
        </div>
        </body></html>
        """;

    [Fact]
    public void OrgPage_ReadsNameCountAndTags()
    {
        var r = OrgPageParser.Parse(OrgPage, "j0k3r5");

        Assert.True(r.IsFound, r.ToString());
        var org = r.Data!;
        Assert.Equal("J0K3R5", org.Sid);
        Assert.Equal("Jokers Gambit", org.LongName);
        Assert.Equal(1104, org.MemberCount);
        Assert.Equal("PMC", org.Archetype);
        Assert.Equal("Regular", org.Commitment);
        Assert.True(org.IsExclusive);
        Assert.Equal(new[] { "Freelancing" }, org.Activities);
    }

    [Fact]
    public void OrgPage_WithoutTagContainer_FallsBackToScanningForKnownWords()
    {
        const string html = """
            <html><body><div id="organization"><h1>Plain Org / PLAIN</h1>
            <div class="stuff"><span>Syndicate</span><span>Hardcore</span><span>Piracy</span></div></div></body></html>
            """;

        var r = OrgPageParser.Parse(html, "PLAIN");

        Assert.True(r.IsFound, r.ToString());
        Assert.Equal("Syndicate", r.Data!.Archetype);
        Assert.Equal("Hardcore", r.Data.Commitment);
        Assert.False(r.Data.IsExclusive);
        Assert.Contains("Piracy", r.Data.Activities);
        Assert.Null(r.Data.MemberCount);
    }

    [Fact]
    public void OrgPage_WithoutHeading_Fails()
    {
        var r = OrgPageParser.Parse("<html><body><h1>Roberts Space Industries</h1></body></html>", "X");

        Assert.Equal(RsiStatus.Failed, r.Status);
    }

    private static string MembersJson(string fragment, object total) =>
        JsonSerializer.Serialize(new { success = 1, code = "OK", msg = "OK", data = new { html = fragment, totalrows = total } });

    [Fact]
    public void MembersResponse_ReadsHandles_SkipsHiddenMembers_CountsEveryEntry()
    {
        const string fragment = """
            <ul>
            <li class="member-item org-visibility-V"><a class="membercard" href="/citizens/Alpha"><span class="name">A. Name</span><span class="nick">Alpha</span></a></li>
            <li class="member-item org-visibility-R"><span class="membercard"><span class="name">REDACTED</span></span></li>
            <li class="member-item org-visibility-V"><a class="membercard" href="/citizens/Beta_2">x</a></li>
            </ul>
            """;

        var r = OrgMembersParser.ParseResponse(MembersJson(fragment, 104));

        Assert.True(r.IsFound, r.ToString());
        Assert.Equal(new[] { "Alpha", "Beta_2" }, r.Data!.Handles);
        Assert.Equal(3, r.Data.EntryCount);
        Assert.Equal(104, r.Data.TotalCount);
    }

    [Fact]
    public void MembersResponse_TotalGivenAsText_IsAccepted()
    {
        var r = OrgMembersParser.ParseResponse(MembersJson("<ul></ul>", "7"));

        Assert.True(r.IsFound, r.ToString());
        Assert.Equal(7, r.Data!.TotalCount);
        Assert.Equal(0, r.Data.EntryCount);
    }

    [Fact]
    public void MembersResponse_ApiFailureOrGarbage_IsFailed()
    {
        var failure = OrgMembersParser.ParseResponse("{\"success\":0,\"code\":\"ErrNoSuchOrg\",\"msg\":\"nope\"}");
        Assert.Equal(RsiStatus.Failed, failure.Status);
        Assert.Contains("nope", failure.Reason);

        Assert.Equal(RsiStatus.Failed, OrgMembersParser.ParseResponse("<html>not json</html>").Status);
        Assert.Equal(RsiStatus.Failed, OrgMembersParser.ParseResponse("[1,2]").Status);
        Assert.Equal(RsiStatus.Failed, OrgMembersParser.ParseResponse("{\"success\":1,\"data\":{}}").Status);
    }
}
