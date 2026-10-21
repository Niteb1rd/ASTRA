namespace Astra.Core.Rsi;

/// <summary>
/// Everything ASTRA asks of RSI. The real implementation is <see cref="RsiClient"/>;
/// tests use a hand-made fake so nothing touches the network.
/// </summary>
public interface IRsiClient
{
    /// <summary>A player's organizations page (main org with attributes, affiliations without).</summary>
    Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default);

    /// <summary>An org's public page: long name, member count and attribute tags.</summary>
    Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default);

    /// <summary>All visible members of an org (pages through RSI's members API).</summary>
    Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default);
}
