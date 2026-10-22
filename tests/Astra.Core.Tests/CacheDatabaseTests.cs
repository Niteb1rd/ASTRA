using Astra.Core.Cache;
using Astra.Core.Rsi;
using Microsoft.Data.Sqlite;
using Xunit;
using static Astra.Core.Tests.CacheEnv;

namespace Astra.Core.Tests;

public class CacheDatabaseTests
{
    [Fact]
    public void PromoteOrgSource_RaisesTheSource_AndKeepsTimesAndData()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgAttributes(CacheEnv.Org("J0K3R5", "PMC", "Piracy"), CacheSource.Hotkey, env.Start);

        env.Db.PromoteOrgSource("j0k3r5", CacheSource.OrgList);

        var org = env.Db.GetOrg("J0K3R5")!;
        Assert.Equal(CacheSource.OrgList, org.Source);
        Assert.Equal(env.Start, org.LastRefreshed);
        Assert.Equal(env.Start, org.AttributesRefreshed);
        Assert.Equal("PMC", org.Archetype);
    }

    [Fact]
    public void PromoteOrgSource_NeverLowers_AndIgnoresUnknownOrgs()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Jokers", new[] { "A_player" }, env.Start);

        env.Db.PromoteOrgSource("J0K3R5", CacheSource.Hotkey);
        env.Db.PromoteOrgSource("NOSUCH", CacheSource.OrgList);

        Assert.Equal(CacheSource.OrgList, env.Db.GetOrg("J0K3R5")!.Source);
        Assert.Null(env.Db.GetOrg("NOSUCH"));
    }

    [Fact]
    public void NewDatabase_HasTheSixTables_AndSchemaVersion2()
    {
        using var env = new CacheEnv();

        var tables = new List<string>();
        using (var command = env.Connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "duplicate_name_counts", "not_found", "org_members", "orgs", "player_orgs", "players" }, tables);
        Assert.Equal(2, env.Db.SchemaVersion);
        Assert.Equal(CacheDatabase.CurrentSchemaVersion, env.Db.SchemaVersion);
    }

    [Fact]
    public void VersionOneDatabase_IsUpgradedToVersionTwo_KeepingItsData()
    {
        using var env = new CacheEnv();
        env.Db.SavePlayer(Player("Nitebird", MainOrg("HEKATE")), CacheSource.PlayerList, env.Start);
        // Turn the file back into what increment 2 wrote: no duplicate_name_counts table, user_version 1.
        RunSql(env.Connection, "DROP TABLE duplicate_name_counts");
        RunSql(env.Connection, "PRAGMA user_version = 1");

        using var upgraded = new CacheDatabase(env.Connection);

        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal(0, upgraded.GetDuplicateFrames("Anything"));
        Assert.Equal(1, upgraded.IncrementDuplicateFrames("Anything", env.Start));
        Assert.Equal("HEKATE", upgraded.GetPlayer("Nitebird")!.Orgs[0].Sid);
    }

    [Fact]
    public void DuplicateFrameCounts_IncrementGetAndDelete_IgnoringCase()
    {
        using var env = new CacheEnv();

        Assert.Equal(0, env.Db.GetDuplicateFrames("Gold"));
        Assert.Equal(1, env.Db.IncrementDuplicateFrames("Gold", env.Start));
        Assert.Equal(2, env.Db.IncrementDuplicateFrames("GOLD", env.Start.AddMinutes(1)));
        Assert.Equal(2, env.Db.GetDuplicateFrames("gold"));
        Assert.Equal(1, env.Db.IncrementDuplicateFrames("Silver", env.Start));

        env.Db.DeleteDuplicateFrames("Gold");

        Assert.Equal(0, env.Db.GetDuplicateFrames("Gold"));
        Assert.Equal(1, env.Db.GetDuplicateFrames("Silver"));
    }

    private static void RunSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void DatabaseFromANewerAstra_IsRefusedWithAClearMessage()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new CacheDatabase(connection));

        Assert.Contains("99", ex.Message);
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void ClosedConnection_IsRefused()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        Assert.Throws<ArgumentException>(() => new CacheDatabase(connection));
    }

    [Fact]
    public void OpeningAnExistingDatabaseAgain_KeepsItsData()
    {
        using var env = new CacheEnv();
        env.Db.SavePlayer(Player("Nitebird", MainOrg("HEKATE")), CacheSource.PlayerList, env.Start);

        var second = new CacheDatabase(env.Connection);

        Assert.NotNull(second.GetPlayer("Nitebird"));
    }

    [Fact]
    public void Player_RoundTrips_WithOrgOrderHiddenOrgsAndAttributes()
    {
        using var env = new CacheEnv();
        var orgs = new[]
        {
            MainOrg("HEKATE", "Organization", "Freelancing", "Social"),
            Affiliate("J0K3R5"),
            Affiliate(null),                 // hidden
            Affiliate("VNGD"),
        };

        env.Db.SavePlayer(Player("Nitebird", orgs), CacheSource.Hotkey, env.Start);
        var cached = env.Db.GetPlayer("NITEBIRD");   // lookups ignore letter case

        Assert.NotNull(cached);
        Assert.Equal("Nitebird", cached!.Handle);   // as RSI shows it
        Assert.Equal(CacheSource.Hotkey, cached.Source);
        Assert.Equal(env.Start, cached.LastRefreshed);
        Assert.Equal(new string?[] { "HEKATE", "J0K3R5", null, "VNGD" }, cached.Orgs.Select(o => o.Sid));
        Assert.True(cached.Orgs[0].IsMain);
        Assert.Equal("Organization", cached.Orgs[0].Archetype);
        Assert.Equal(new[] { "Freelancing", "Social" }, cached.Orgs[0].ActivityList);
        Assert.True(cached.Orgs[2].IsRedacted);
        Assert.Empty(cached.Orgs[1].ActivityList);
    }

    [Fact]
    public void SavingAPlayerAgain_ReplacesTheOldOrgs()
    {
        using var env = new CacheEnv();
        env.Db.SavePlayer(Player("Nitebird", MainOrg("HEKATE"), Affiliate("J0K3R5"), Affiliate("VNGD")), CacheSource.Hotkey, env.Start);

        env.Db.SavePlayer(Player("Nitebird", MainOrg("OTHER")), CacheSource.Hotkey, env.Start.AddDays(1));

        var cached = env.Db.GetPlayer("Nitebird")!;
        Assert.Equal(new string?[] { "OTHER" }, cached.Orgs.Select(o => o.Sid));
        Assert.Equal(env.Start.AddDays(1), cached.LastRefreshed);
    }

    [Fact]
    public void UnknownPlayerOrOrg_ReturnsNull()
    {
        using var env = new CacheEnv();

        Assert.Null(env.Db.GetPlayer("Nobody"));
        Assert.Null(env.Db.GetOrg("NOBODY"));
        Assert.Empty(env.Db.GetOrgMembers("NOBODY"));
    }

    [Fact]
    public void Source_NeverGetsWeaker()
    {
        using var env = new CacheEnv();
        env.Db.SavePlayer(Player("Nitebird", MainOrg("HEKATE")), CacheSource.PlayerList, env.Start);

        env.Db.SavePlayer(Player("Nitebird", MainOrg("HEKATE")), CacheSource.Hotkey, env.Start.AddDays(40));
        Assert.Equal(CacheSource.PlayerList, env.Db.GetPlayer("Nitebird")!.Source);

        env.Db.SavePlayer(Player("Other", MainOrg("X")), CacheSource.Hotkey, env.Start);
        env.Db.SavePlayer(Player("Other", MainOrg("X")), CacheSource.OrgListMember, env.Start);
        Assert.Equal(CacheSource.OrgListMember, env.Db.GetPlayer("Other")!.Source);

        env.Db.SavePlayer(Player("Other", MainOrg("X")), CacheSource.PlayerList, env.Start);
        Assert.Equal(CacheSource.PlayerList, env.Db.GetPlayer("Other")!.Source);
    }

    [Fact]
    public void SourcesAreStoredAsTheDocumentedWords()
    {
        Assert.Equal("player_list", CacheSources.ToDb(CacheSource.PlayerList));
        Assert.Equal("org_list", CacheSources.ToDb(CacheSource.OrgList));
        Assert.Equal("org_list_member", CacheSources.ToDb(CacheSource.OrgListMember));
        Assert.Equal("hotkey", CacheSources.ToDb(CacheSource.Hotkey));
        foreach (var source in Enum.GetValues<CacheSource>())
            Assert.Equal(source, CacheSources.FromDb(CacheSources.ToDb(source)));
    }

    [Fact]
    public void OrgListEntry_StoresLongNameAndMembers_Only()
    {
        using var env = new CacheEnv();

        env.Db.SaveOrgListEntry("J0K3R5", "Jokers Gambit", new[] { "Alice", "Bobby", "alice" }, env.Start);

        var org = env.Db.GetOrg("j0k3r5")!;
        Assert.Equal("Jokers Gambit", org.LongName);
        Assert.Equal(CacheSource.OrgList, org.Source);
        Assert.Equal(env.Start, org.MembersRefreshed);
        Assert.Null(org.AttributesRefreshed);
        Assert.False(org.HasAttributes);
        Assert.Null(org.Archetype);
        Assert.Equal(new[] { "Alice", "Bobby" }, env.Db.GetOrgMembers("J0K3R5"));   // duplicates (ignoring case) collapse
    }

    [Fact]
    public void OrgListEntry_Refreshed_ReplacesTheMemberList()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Jokers Gambit", new[] { "Alice", "Bobby" }, env.Start);

        env.Db.SaveOrgListEntry("J0K3R5", "Jokers Gambit II", new[] { "Bobby", "Carol" }, env.Start.AddDays(5));

        Assert.Equal(new[] { "Bobby", "Carol" }, env.Db.GetOrgMembers("J0K3R5"));
        var org = env.Db.GetOrg("J0K3R5")!;
        Assert.Equal("Jokers Gambit II", org.LongName);
        Assert.Equal(env.Start.AddDays(5), org.MembersRefreshed);
    }

    [Fact]
    public void OrgAttributes_FromAHotkeyLookup_DoNotTouchTheMemberListOfAListedOrg()
    {
        using var env = new CacheEnv();
        env.Db.SaveOrgListEntry("J0K3R5", "Jokers Gambit", new[] { "Alice" }, env.Start);

        env.Db.SaveOrgAttributes(Org("J0K3R5", "PMC", "Freelancing"), CacheSource.Hotkey, env.Start.AddDays(2));

        var org = env.Db.GetOrg("J0K3R5")!;
        Assert.Equal(CacheSource.OrgList, org.Source);                // stays a listed org
        Assert.Equal(env.Start, org.MembersRefreshed);                // member list timestamp untouched
        Assert.Equal(env.Start.AddDays(2), org.AttributesRefreshed);
        Assert.Equal("PMC", org.Archetype);
        Assert.Equal("Regular", org.Commitment);
        Assert.True(org.IsExclusive);
        Assert.Equal(new[] { "Freelancing" }, org.Activities);
        Assert.Equal(new[] { "Alice" }, env.Db.GetOrgMembers("J0K3R5"));
    }

    [Fact]
    public void OrgAttributesOnly_HasNoMemberListTimestamp_AndHotkeySource()
    {
        using var env = new CacheEnv();

        env.Db.SaveOrgAttributes(Org("VNGD"), CacheSource.Hotkey, env.Start);

        var org = env.Db.GetOrg("VNGD")!;
        Assert.Equal(CacheSource.Hotkey, org.Source);
        Assert.Null(org.MembersRefreshed);
        Assert.Equal(100, org.MemberCount);
    }

    [Fact]
    public void NotFound_IsRememberedFor30Days_ThenUnknown()
    {
        using var env = new CacheEnv();
        env.Db.SaveNotFound(NotFoundKind.Player, "Ghost", CacheSource.Hotkey, env.Start);

        Assert.True(env.Db.IsKnownNotFound("Ghost", env.Start));
        Assert.True(env.Db.IsKnownNotFound("ghost", env.Start.AddDays(29).AddHours(23)));
        Assert.False(env.Db.IsKnownNotFound("Ghost", env.Start.AddDays(30)));
        Assert.False(env.Db.IsKnownNotFound("Ghost", env.Start.AddDays(31)));
        Assert.False(env.Db.IsKnownNotFound("SomeoneElse", env.Start));
        Assert.NotNull(env.Db.GetNotFoundTime(NotFoundKind.Player, "Ghost"));   // the note itself stays; callers decide about retrying
    }

    [Fact]
    public void NotFound_ForPlayersAndOrgs_AreSeparate()
    {
        using var env = new CacheEnv();
        env.Db.SaveNotFound(NotFoundKind.Org, "NOSUCH", CacheSource.OrgList, env.Start);

        Assert.True(env.Db.IsKnownOrgNotFound("NOSUCH", env.Start));
        Assert.False(env.Db.IsKnownNotFound("NOSUCH", env.Start));
    }

    [Fact]
    public void NotFound_DropsCachedData_AndSavingTheDataAgainDropsTheNote()
    {
        using var env = new CacheEnv();
        env.Db.SavePlayer(Player("Renamed", MainOrg("X")), CacheSource.Hotkey, env.Start);

        env.Db.SaveNotFound(NotFoundKind.Player, "Renamed", CacheSource.Hotkey, env.Start.AddDays(1));
        Assert.Null(env.Db.GetPlayer("Renamed"));
        Assert.True(env.Db.IsKnownNotFound("Renamed", env.Start.AddDays(1)));

        env.Db.SavePlayer(Player("Renamed", MainOrg("X")), CacheSource.Hotkey, env.Start.AddDays(2));
        Assert.NotNull(env.Db.GetPlayer("Renamed"));
        Assert.False(env.Db.IsKnownNotFound("Renamed", env.Start.AddDays(2)));
    }
}
