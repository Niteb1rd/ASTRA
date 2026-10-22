using Iris.Core.Cache;
using Iris.Core.Config;
using Iris.Core.Rsi;
using Iris.Core.Social;
using Microsoft.Data.Sqlite;

namespace Iris.Core.Tests;

/// <summary>
/// Everything a cache test needs: an in-memory SQLite database (kept open for the whole test),
/// a fake clock and a fake RSI client. Nothing touches the disk or the network.
/// </summary>
internal sealed class CacheEnv : IDisposable
{
    public CacheEnv()
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();
        Db = new CacheDatabase(Connection);
    }

    public SqliteConnection Connection { get; }
    public CacheDatabase Db { get; }
    public FakeClock Clock { get; } = new();
    public FakeRsiClient Rsi { get; } = new();
    public ListLog Log { get; } = new();

    public DateTimeOffset Start => new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public CacheRefresher Refresher(int ttlDays = 30) => new(Db, Rsi, Clock, new CacheSettings(ttlDays), Log);

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }

    // ----- small builders used by the tests -----

    public static OrgMembership MainOrg(string? sid, string? archetype = null, params string[] activities) =>
        new(sid, true, archetype, activities);

    public static OrgMembership Affiliate(string? sid) => new(sid, false);

    public static PlayerOrgs Player(string handle, params OrgMembership[] orgs) => new(handle, orgs);

    public static OrgInfo Org(string sid, string archetype = "PMC", params string[] activities) =>
        new(sid, "Long name of " + sid, 100, archetype, "Regular", true, activities);

    public static RsiResult<T> Found<T>(T data) => RsiResult<T>.Found(data);

    public static SocialConfig ConfigWith(string[] players, string[] orgs)
    {
        var config = new SocialConfig();
        foreach (var p in players)
            config.Players.Add(new PlayerEntry(p, OverlayColor.Cyan));
        int priority = 1;
        foreach (var o in orgs)
            config.Orgs.Add(new OrgEntry(o, priority++, OverlayColor.Red));
        return config;
    }
}
