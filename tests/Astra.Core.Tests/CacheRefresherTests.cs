using Astra.Core.Cache;
using Astra.Core.Rsi;
using Xunit;
using static Astra.Core.Tests.CacheEnv;

namespace Astra.Core.Tests;

public class CacheRefresherTests
{
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    // ------------------------------------------------------------------ TTL and staleness

    [Fact]
    public async Task Ttl30Days_Versus7Days_DecidesWhatIsStale()
    {
        using var env = new CacheEnv();
        var config = ConfigWith(new[] { "Nitebird" }, new[] { "J0K3R5" });
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5"));
        env.Rsi.Members["J0K3R5"] = Found(new OrgMembers("J0K3R5", new[] { "Alice" }));

        // nothing cached yet: everything on the lists is missing, so stale
        Assert.Equal(new[] { "Nitebird" }, env.Refresher().FindStale(config).Players);
        Assert.Equal(new[] { "J0K3R5" }, env.Refresher().FindStale(config).Orgs);

        var summary = await env.Refresher().RefreshStaleAsync(config);
        Assert.Equal(1, summary.PlayersRefreshed);
        Assert.Equal(1, summary.OrgsRefreshed);
        Assert.Empty(summary.Failures);
        Assert.True(env.Refresher().FindStale(config).IsEmpty);

        env.Clock.Advance(TimeSpan.FromDays(10));
        Assert.True(env.Refresher(30).FindStale(config).IsEmpty);          // 10 days old: fresh under 30
        var stale7 = env.Refresher(7).FindStale(config);                   // ... but stale under 7
        Assert.Equal(new[] { "Nitebird" }, stale7.Players);
        Assert.Equal(new[] { "J0K3R5" }, stale7.Orgs);

        env.Clock.Advance(TimeSpan.FromDays(21));                          // 31 days old
        Assert.False(env.Refresher(30).FindStale(config).IsEmpty);
    }

    [Fact]
    public async Task EntryExactlyAsOldAsTheTtl_IsNotStale_OneSecondOlderIs()
    {
        using var env = new CacheEnv();
        var config = ConfigWith(new[] { "Nitebird" }, Array.Empty<string>());
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));
        await env.Refresher().RefreshStaleAsync(config);

        env.Clock.Advance(TimeSpan.FromDays(7));
        Assert.True(env.Refresher(7).FindStale(config).IsEmpty);

        env.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "Nitebird" }, env.Refresher(7).FindStale(config).Players);
    }

    [Fact]
    public void OnlySevenAndThirtyDaysAreAllowedAsTtl()
    {
        Assert.Equal(30, new CacheSettings().TtlDays);
        Assert.Equal(TimeSpan.FromDays(7), new CacheSettings(7).Ttl);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CacheSettings(14));
    }

    [Fact]
    public async Task AutomaticRefresh_OnlyFetchesStaleEntries()
    {
        using var env = new CacheEnv();
        var config = ConfigWith(new[] { "Fresh", "Old" }, Array.Empty<string>());
        env.Rsi.Players["Old"] = Found(Player("Old", MainOrg("A")));
        await env.Refresher().RefreshStaleAsync(ConfigWith(new[] { "Old" }, Array.Empty<string>()));
        env.Clock.Advance(TimeSpan.FromDays(40));
        env.Rsi.Players["Fresh"] = Found(Player("Fresh", MainOrg("B")));
        await env.Refresher().RefreshStaleAsync(ConfigWith(new[] { "Fresh" }, Array.Empty<string>()));
        env.Rsi.Calls.Clear();

        await env.Refresher().RefreshStaleAsync(config);

        Assert.Equal(new[] { "player:Old" }, env.Rsi.Calls);
    }

    // ------------------------------------------------------------------ manual "Update database now"

    [Fact]
    public async Task ManualUpdate_RefreshesListEntries_AndNeverHotkeyOrOrgListMemberEntries()
    {
        using var env = new CacheEnv();
        var t0 = env.Start;
        env.Db.SavePlayer(Player("Nitebird", MainOrg("OLD")), CacheSource.PlayerList, t0);
        env.Db.SavePlayer(Player("HotkeyGuy", MainOrg("X")), CacheSource.Hotkey, t0);
        env.Db.SavePlayer(Player("MemberGuy", MainOrg("Y")), CacheSource.OrgListMember, t0);
        env.Db.SaveOrgAttributes(Org("VNGD"), CacheSource.Hotkey, t0);
        env.Db.SaveOrgListEntry("J0K3R5", "Old name", new[] { "OldMember" }, t0);
        env.Clock.Advance(TimeSpan.FromDays(100));   // every entry is stale by now

        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5"));
        env.Rsi.Members["J0K3R5"] = Found(new OrgMembers("J0K3R5", new[] { "NewMember" }));

        var summary = await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(new[] { "Nitebird" }, new[] { "J0K3R5" }));

        // only the two list entries were requested ...
        Assert.Equal(new[] { "player:Nitebird", "org:J0K3R5", "members:J0K3R5" }, env.Rsi.Calls);
        Assert.Equal(1, summary.PlayersRefreshed);
        Assert.Equal(1, summary.OrgsRefreshed);
        // ... and they were rewritten, while the others kept their old timestamps
        Assert.Equal(env.Clock.UtcNow, env.Db.GetPlayer("Nitebird")!.LastRefreshed);
        Assert.Equal("HEKATE", env.Db.GetPlayer("Nitebird")!.Orgs[0].Sid);
        Assert.Equal(new[] { "NewMember" }, env.Db.GetOrgMembers("J0K3R5"));
        Assert.Equal(t0, env.Db.GetPlayer("HotkeyGuy")!.LastRefreshed);
        Assert.Equal(t0, env.Db.GetPlayer("MemberGuy")!.LastRefreshed);
        Assert.Equal(t0, env.Db.GetOrg("VNGD")!.LastRefreshed);
    }

    [Fact]
    public async Task ManualUpdate_RefreshesEvenFreshEntries_AutomaticRefreshDoesNot()
    {
        using var env = new CacheEnv();
        var config = ConfigWith(new[] { "Nitebird" }, Array.Empty<string>());
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));
        await env.Refresher().RefreshStaleAsync(config);
        env.Rsi.Calls.Clear();
        env.Clock.Advance(OneDay);

        await env.Refresher().RefreshStaleAsync(config);
        Assert.Empty(env.Rsi.Calls);

        await env.Refresher().UpdateDatabaseNowAsync(config);
        Assert.Equal(new[] { "player:Nitebird" }, env.Rsi.Calls);
    }

    [Fact]
    public async Task ListedPlayerAlreadyCachedFromAHotkeyLookup_BecomesAPlayerListEntry_WhenRefreshedFromTheList()
    {
        using var env = new CacheEnv();
        env.Db.SavePlayer(Player("Nitebird", MainOrg("HEKATE")), CacheSource.Hotkey, env.Start);
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));

        await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(new[] { "Nitebird" }, Array.Empty<string>()));

        Assert.Equal(CacheSource.PlayerList, env.Db.GetPlayer("Nitebird")!.Source);
    }

    [Fact]
    public async Task ListRefresh_StoresOnlyThePrimaryPage_AndOnlyLongNameAndMembersForOrgs()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE", "Organization", "Social"), Affiliate("J0K3R5"), Affiliate("VNGD")));
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5", "PMC", "Freelancing"));
        env.Rsi.Members["J0K3R5"] = Found(new OrgMembers("J0K3R5", new[] { "Alice", "Bob" }));

        await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(new[] { "Nitebird" }, new[] { "J0K3R5" }));

        // no drill-down: nobody asked for HEKATE, VNGD, Alice or Bob
        Assert.Equal(new[] { "player:Nitebird", "org:J0K3R5", "members:J0K3R5" }, env.Rsi.Calls);
        var org = env.Db.GetOrg("J0K3R5")!;
        Assert.Equal("Long name of J0K3R5", org.LongName);
        Assert.Equal(CacheSource.OrgList, org.Source);
        Assert.Null(org.Archetype);                // attributes are not stored by the list refresh
        Assert.False(org.HasAttributes);
        Assert.Null(env.Db.GetOrg("VNGD"));
        Assert.Null(env.Db.GetPlayer("Alice"));
    }

    [Fact]
    public async Task ListRefresh_FailuresAreReported_AndKeepTheOldData()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Old name", new[] { "OldMember" }, env.Start);
        env.Db.SavePlayer(Player("Nitebird", MainOrg("OLD")), CacheSource.PlayerList, env.Start);
        env.Clock.Advance(TimeSpan.FromDays(40));
        env.Rsi.Players["Nitebird"] = RsiResult<PlayerOrgs>.Failed("timeout after 15 s");
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5"));
        env.Rsi.Members["J0K3R5"] = RsiResult<OrgMembers>.Failed("HTTP 503");

        var summary = await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(new[] { "Nitebird" }, new[] { "J0K3R5" }));

        Assert.Equal(0, summary.PlayersRefreshed + summary.OrgsRefreshed);
        Assert.Equal(2, summary.Failures.Count);
        Assert.Contains(summary.Failures, f => f.Contains("Nitebird") && f.Contains("timeout"));
        Assert.Contains(summary.Failures, f => f.Contains("J0K3R5") && f.Contains("503"));
        Assert.Equal("OLD", env.Db.GetPlayer("Nitebird")!.Orgs[0].Sid);
        Assert.Equal(new[] { "OldMember" }, env.Db.GetOrgMembers("J0K3R5"));
        Assert.Equal("Old name", env.Db.GetOrg("J0K3R5")!.LongName);
    }

    [Fact]
    public async Task ListedOrgThatReturns404_IsNegativeCached_AndNotRetriedAutomatically()
    {
        using var env = new CacheEnv();
        var config = ConfigWith(Array.Empty<string>(), new[] { "TYPO" });
        env.Rsi.OrgInfos["TYPO"] = RsiResult<OrgInfo>.NotFound();

        var summary = await env.Refresher().RefreshStaleAsync(config);

        Assert.Equal(1, summary.OrgsNotFound);
        Assert.True(env.Db.IsKnownOrgNotFound("TYPO", env.Clock.UtcNow));
        Assert.True(env.Refresher().FindStale(config).IsEmpty);
        env.Clock.Advance(TimeSpan.FromDays(60));
        Assert.True(env.Refresher().FindStale(config).IsEmpty);   // even after the note expired: not retried by itself
    }

    // ------------------------------------------------------------------ negative cache

    [Fact]
    public async Task Http404_IsNegativeCached_ForThirtyDays_ThenRetriedWhenEncounteredAgain()
    {
        using var env = new CacheEnv();
        var refresher = env.Refresher();
        env.Rsi.Players["Ghost"] = RsiResult<PlayerOrgs>.NotFound();

        var first = await refresher.LookupPlayerForHotkeyAsync("Ghost");
        Assert.Equal(PlayerLookupStatus.NotFound, first.Status);
        Assert.False(first.FromCache);
        Assert.True(env.Db.IsKnownNotFound("Ghost", env.Clock.UtcNow));

        env.Clock.Advance(TimeSpan.FromDays(29));
        var second = await refresher.LookupPlayerForHotkeyAsync("Ghost");
        Assert.Equal(PlayerLookupStatus.NotFound, second.Status);
        Assert.True(second.FromCache);
        Assert.Single(env.Rsi.Calls);               // answered from the negative cache

        env.Clock.Advance(TimeSpan.FromDays(2));    // 31 days: the note has expired
        env.Rsi.Players["Ghost"] = Found(Player("Ghost", MainOrg("NEWORG")));
        var third = await refresher.LookupPlayerForHotkeyAsync("Ghost");
        Assert.Equal(PlayerLookupStatus.Found, third.Status);
        Assert.Equal(2, env.Rsi.Calls.Count(c => c == "player:Ghost"));   // asked RSI again
        Assert.False(env.Db.IsKnownNotFound("Ghost", env.Clock.UtcNow));
    }

    [Fact]
    public async Task Timeouts_AndOtherFailures_AreNeverNegativeCached()
    {
        using var env = new CacheEnv();
        var refresher = env.Refresher();
        env.Rsi.Players["Slow"] = RsiResult<PlayerOrgs>.Failed("timeout after 15 s");

        var first = await refresher.LookupPlayerForHotkeyAsync("Slow");

        Assert.Equal(PlayerLookupStatus.Failed, first.Status);
        Assert.Contains("timeout", first.Reason);
        Assert.False(env.Db.IsKnownNotFound("Slow", env.Clock.UtcNow));
        Assert.Null(env.Db.GetNotFoundTime(NotFoundKind.Player, "Slow"));
        Assert.Null(env.Db.GetPlayer("Slow"));

        var second = await refresher.LookupPlayerForHotkeyAsync("Slow");   // tries again straight away
        Assert.Equal(PlayerLookupStatus.Failed, second.Status);
        Assert.Equal(2, env.Rsi.Calls.Count);
    }

    [Fact]
    public async Task ListedPlayerWithAnExpiredNotFoundNote_IsNotRetriedAutomatically_ButManualUpdateTriesAgain()
    {
        using var env = new CacheEnv();
        env.Db.SaveNotFound(NotFoundKind.Player, "Ghost", CacheSource.PlayerList, env.Start);
        env.Clock.Advance(TimeSpan.FromDays(60));
        var config = ConfigWith(new[] { "Ghost" }, Array.Empty<string>());
        env.Rsi.Players["Ghost"] = Found(Player("Ghost", MainOrg("X")));

        Assert.True(env.Refresher().FindStale(config).IsEmpty);
        await env.Refresher().RefreshStaleAsync(config);
        Assert.Empty(env.Rsi.Calls);

        await env.Refresher().UpdateDatabaseNowAsync(config);
        Assert.Equal(new[] { "player:Ghost" }, env.Rsi.Calls);
        Assert.NotNull(env.Db.GetPlayer("Ghost"));
        Assert.Null(env.Db.GetNotFoundTime(NotFoundKind.Player, "Ghost"));
    }

    // ------------------------------------------------------------------ hotkey path

    [Fact]
    public async Task Hotkey_FetchesAffiliateOrgPages_StoresThemAsHotkey_AndUsesTheCacheNextTime()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird",
            MainOrg("HEKATE", "Organization", "Freelancing", "Social"),
            Affiliate("J0K3R5"),
            Affiliate("AVOCADO"),
            Affiliate(null)));
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5", "PMC", "Freelancing"));
        env.Rsi.OrgInfos["AVOCADO"] = Found(Org("AVOCADO", "Club", "Social"));

        var first = await env.Refresher().LookupPlayerForHotkeyAsync("nitebird");

        // main org is not looked up (its attributes came with the player page), hidden orgs have nothing to look up
        Assert.Equal(new[] { "player:nitebird", "org:J0K3R5", "org:AVOCADO" }, env.Rsi.Calls);
        Assert.Equal(PlayerLookupStatus.Found, first.Status);
        Assert.False(first.FromCache);
        Assert.Equal("Nitebird", first.Handle);
        Assert.Equal("Organization", first.Orgs[0].Archetype);
        Assert.Equal("PMC", first.Orgs[1].Archetype);
        Assert.Equal(new[] { "Freelancing" }, first.Orgs[1].ActivityList);
        Assert.Equal("Club", first.Orgs[2].Archetype);
        Assert.True(first.Orgs[3].IsRedacted);

        Assert.Equal(CacheSource.Hotkey, env.Db.GetPlayer("Nitebird")!.Source);
        var org = env.Db.GetOrg("J0K3R5")!;
        Assert.Equal(CacheSource.Hotkey, org.Source);
        Assert.Equal(env.Clock.UtcNow, org.AttributesRefreshed);
        Assert.Null(org.MembersRefreshed);

        env.Rsi.Calls.Clear();
        env.Clock.Advance(TimeSpan.FromDays(5));
        var second = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");

        Assert.Empty(env.Rsi.Calls);   // everything from the cache
        Assert.True(second.FromCache);
        Assert.False(second.IsStale);
        Assert.Equal("PMC", second.Orgs[1].Archetype);
        Assert.Equal("Club", second.Orgs[2].Archetype);
    }

    [Fact]
    public async Task Hotkey_OrgPageFailure_LeavesThatOrgWithoutAttributes_AndIsNotNegativeCached()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE"), Affiliate("J0K3R5")));
        env.Rsi.OrgInfos["J0K3R5"] = RsiResult<OrgInfo>.Failed("timeout after 15 s");

        var result = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");

        Assert.Equal(PlayerLookupStatus.Found, result.Status);
        Assert.Null(result.Orgs[1].Archetype);
        Assert.False(env.Db.IsKnownOrgNotFound("J0K3R5", env.Clock.UtcNow));
        Assert.Null(env.Db.GetOrg("J0K3R5"));

        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5"));
        env.Rsi.Calls.Clear();
        var again = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");
        Assert.Equal(new[] { "org:J0K3R5" }, env.Rsi.Calls);   // player page cached, org page asked again
        Assert.Equal("PMC", again.Orgs[1].Archetype);
    }

    [Fact]
    public async Task Hotkey_404ForAnOrgOnTheOrgList_LeavesTheListOrgUntouched()
    {
        using var env = new CacheEnv();
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5"));
        env.Rsi.Members["J0K3R5"] = Found(new OrgMembers("J0K3R5", new[] { "Alice", "Bob" }));
        await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(Array.Empty<string>(), new[] { "J0K3R5" }));
        Assert.Equal(CacheSource.OrgList, env.Db.GetOrg("J0K3R5")!.Source);

        // a later hotkey lookup gets a (wrong or temporary) 404 for that org's page
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE"), Affiliate("J0K3R5")));
        env.Rsi.OrgInfos["J0K3R5"] = RsiResult<OrgInfo>.NotFound();
        await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");

        Assert.False(env.Db.IsKnownOrgNotFound("J0K3R5", env.Clock.UtcNow));
        Assert.Equal(CacheSource.OrgList, env.Db.GetOrg("J0K3R5")!.Source);
        Assert.Equal(2, env.Db.GetOrgMembers("J0K3R5").Count);
    }

    [Fact]
    public async Task Hotkey_AffiliateOrg404_IsNegativeCached()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE"), Affiliate("GONE")));
        env.Rsi.OrgInfos["GONE"] = RsiResult<OrgInfo>.NotFound();

        await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");
        env.Rsi.Calls.Clear();
        var again = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");

        Assert.Empty(env.Rsi.Calls);
        Assert.Null(again.Orgs[1].Archetype);
        Assert.True(env.Db.IsKnownOrgNotFound("GONE", env.Clock.UtcNow));
    }

    [Fact]
    public async Task Hotkey_StaleEntry_IsRefetched_AndOldDataIsUsedWhenTheRefetchFails()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));
        await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");
        env.Clock.Advance(TimeSpan.FromDays(31));
        env.Rsi.Players["Nitebird"] = RsiResult<PlayerOrgs>.Failed("HTTP 503");

        var result = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");

        Assert.Equal(PlayerLookupStatus.Found, result.Status);
        Assert.True(result.IsStale);
        Assert.True(result.FromCache);
        Assert.Equal("HEKATE", result.Orgs[0].Sid);

        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("NEWMAIN")));
        var fresh = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");
        Assert.False(fresh.IsStale);
        Assert.Equal("NEWMAIN", fresh.Orgs[0].Sid);
    }

    [Fact]
    public async Task Hotkey_OnAListedPlayer_KeepsThePlayerListSource()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE")));
        await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(new[] { "Nitebird" }, Array.Empty<string>()));
        env.Clock.Advance(TimeSpan.FromDays(40));

        await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");   // stale, so refetched by the hotkey path

        Assert.Equal(CacheSource.PlayerList, env.Db.GetPlayer("Nitebird")!.Source);
        Assert.Equal(env.Clock.UtcNow, env.Db.GetPlayer("Nitebird")!.LastRefreshed);
    }

    [Fact]
    public async Task Hotkey_AttributesOfAListedOrg_KeepTheOrgListSource()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Jokers Gambit", new[] { "Alice" }, env.Start);
        env.Rsi.Players["Nitebird"] = Found(Player("Nitebird", MainOrg("HEKATE"), Affiliate("J0K3R5")));
        env.Rsi.OrgInfos["J0K3R5"] = Found(Org("J0K3R5", "PMC", "Freelancing"));

        var result = await env.Refresher().LookupPlayerForHotkeyAsync("Nitebird");

        Assert.Equal("PMC", result.Orgs[1].Archetype);
        var org = env.Db.GetOrg("J0K3R5")!;
        Assert.Equal(CacheSource.OrgList, org.Source);
        Assert.Equal(new[] { "Alice" }, env.Db.GetOrgMembers("J0K3R5"));
    }

    [Fact]
    public async Task Lookup_WithAnInvalidName_FailsWithoutAskingRsi()
    {
        using var env = new CacheEnv();

        var result = await env.Refresher().LookupPlayerForHotkeyAsync("bad name/..");

        Assert.Equal(PlayerLookupStatus.Failed, result.Status);
        Assert.Equal("invalid name", result.Reason);
        Assert.Empty(env.Rsi.Calls);
    }

    // ------------------------------------------------------------------ org-list members

    [Fact]
    public async Task OrgListMember_OnlyThePrimaryPageIsFetched_StoredAsOrgListMember()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Newbie"] = Found(Player("Newbie", MainOrg("J0K3R5"), Affiliate("VNGD"), Affiliate("AVOCADO")));

        var result = await env.Refresher().LookupOrgListMemberAsync("Newbie");

        Assert.Equal(PlayerLookupStatus.Found, result.Status);
        Assert.Equal(new[] { "player:Newbie" }, env.Rsi.Calls);   // no org pages
        Assert.Equal(CacheSource.OrgListMember, env.Db.GetPlayer("Newbie")!.Source);
        Assert.Null(env.Db.GetOrg("VNGD"));
    }

    [Fact]
    public async Task OrgListMember_SecondLookup_ComesFromTheCache()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Newbie"] = Found(Player("Newbie", MainOrg("J0K3R5")));
        await env.Refresher().LookupOrgListMemberAsync("Newbie");
        env.Clock.Advance(TimeSpan.FromDays(3));

        var again = await env.Refresher().LookupOrgListMemberAsync("Newbie");

        Assert.True(again.FromCache);
        Assert.Single(env.Rsi.Calls);
    }

    [Fact]
    public async Task OrgListMember_404_IsNegativeCached()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Gone"] = RsiResult<PlayerOrgs>.NotFound();

        await env.Refresher().LookupOrgListMemberAsync("Gone");
        var again = await env.Refresher().LookupOrgListMemberAsync("Gone");

        Assert.Equal(PlayerLookupStatus.NotFound, again.Status);
        Assert.Single(env.Rsi.Calls);
    }

    // ------------------------------------------------------------------ logging of failures

    [Fact]
    public async Task FailedRefreshes_AreLogged()
    {
        using var env = new CacheEnv();
        env.Rsi.Players["Slow"] = RsiResult<PlayerOrgs>.Failed("timeout after 15 s");

        await env.Refresher().UpdateDatabaseNowAsync(ConfigWith(new[] { "Slow" }, Array.Empty<string>()));

        Assert.Contains(env.Log.Entries, e => e.Message.Contains("Slow") && e.Message.Contains("timeout"));
    }
}
