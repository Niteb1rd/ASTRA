using Iris.Core;
using Iris.Core.Rsi;

namespace Iris.Core.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>A log that remembers every line.</summary>
internal sealed class ListLog : ILog
{
    public List<(DateTimeOffset Time, string Message)> Entries { get; } = new();

    public void Write(DateTimeOffset utcTime, string message) => Entries.Add((utcTime, message));
}

/// <summary>
/// An RSI client with scripted answers. Anything not scripted fails with "not scripted",
/// so a test notices when code asks for something unexpected. Every call is recorded in <see cref="Calls"/>
/// as "player:Name", "org:SID" or "members:SID".
/// </summary>
internal sealed class FakeRsiClient : IRsiClient
{
    public Dictionary<string, RsiResult<PlayerOrgs>> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RsiResult<OrgInfo>> OrgInfos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RsiResult<OrgMembers>> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = new();

    public Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default)
    {
        Calls.Add("player:" + handle);
        return Task.FromResult(Players.TryGetValue(handle, out var r) ? r : RsiResult<PlayerOrgs>.Failed("not scripted"));
    }

    public Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default)
    {
        Calls.Add("org:" + sid);
        return Task.FromResult(OrgInfos.TryGetValue(sid, out var r) ? r : RsiResult<OrgInfo>.Failed("not scripted"));
    }

    public Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default)
    {
        Calls.Add("members:" + sid);
        return Task.FromResult(Members.TryGetValue(sid, out var r) ? r : RsiResult<OrgMembers>.Failed("not scripted"));
    }
}
