using Iris.Core.Social;

namespace Iris.Core.Rsi;

/// <summary>
/// What a player's "organizations" page tells us.
/// </summary>
/// <param name="Handle">The handle as RSI shows it (RSI's capitalisation).</param>
/// <param name="Orgs">
/// Main org first, then affiliations in page order. A hidden org has <c>Sid == null</c>.
/// Only the main org carries attributes here; affiliates come with none (they are filled in from org pages later).
/// </param>
public sealed record PlayerOrgs(string Handle, IReadOnlyList<OrgMembership> Orgs);

/// <summary>What an org's public page tells us.</summary>
/// <param name="Sid">Short name, e.g. J0K3R5.</param>
/// <param name="LongName">Full name, e.g. "Jokers Gambit".</param>
/// <param name="MemberCount">Number of members RSI shows, if we could read it.</param>
/// <param name="Archetype">PMC, Syndicate, Corporation, Organization, Faith or Club (null if not shown).</param>
/// <param name="Commitment">Casual, Regular or Hardcore (null if not shown).</param>
/// <param name="IsExclusive">True when the org is tagged "Exclusive".</param>
/// <param name="Activities">Focus activities, e.g. Freelancing, Exploration.</param>
public sealed record OrgInfo(
    string Sid,
    string LongName,
    int? MemberCount,
    string? Archetype,
    string? Commitment,
    bool IsExclusive,
    IReadOnlyList<string> Activities);

/// <summary>The handles of an org's (visible) members. Hidden members are left out.</summary>
public sealed record OrgMembers(string Sid, IReadOnlyList<string> Handles);

/// <summary>One page of the members API, after parsing.</summary>
/// <param name="Handles">Handles found on this page (hidden members skipped).</param>
/// <param name="EntryCount">How many member entries the page held, hidden ones included. Zero means "no more pages".</param>
/// <param name="TotalCount">Total member count RSI reports, if present.</param>
public sealed record OrgMembersPage(IReadOnlyList<string> Handles, int EntryCount, int? TotalCount);
