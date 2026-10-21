using Astra.Core.Social;

namespace Astra.Core.Cache;

/// <summary>
/// Why an entry is in the cache. Decides what "Update database now" may refresh:
/// only <see cref="PlayerList"/> and <see cref="OrgList"/> entries (the user's own lists).
/// The numbers say how strong a source is: when an entry is written again from a weaker source,
/// it keeps the stronger one (a player on the user's list stays 'player_list' even after a hotkey lookup).
/// </summary>
public enum CacheSource
{
    /// <summary>Looked up because the user pressed the hotkey on a nameplate.</summary>
    Hotkey = 0,

    /// <summary>A member of a listed org, looked up when their nameplate was seen.</summary>
    OrgListMember = 1,

    /// <summary>A player on the user's player list.</summary>
    PlayerList = 2,

    /// <summary>An org on the user's org list.</summary>
    OrgList = 3,
}

/// <summary>Converts <see cref="CacheSource"/> to and from the text stored in the database.</summary>
public static class CacheSources
{
    public static string ToDb(CacheSource source) => source switch
    {
        CacheSource.Hotkey => "hotkey",
        CacheSource.OrgListMember => "org_list_member",
        CacheSource.PlayerList => "player_list",
        CacheSource.OrgList => "org_list",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static CacheSource FromDb(string text) => text switch
    {
        "hotkey" => CacheSource.Hotkey,
        "org_list_member" => CacheSource.OrgListMember,
        "player_list" => CacheSource.PlayerList,
        "org_list" => CacheSource.OrgList,
        _ => throw new InvalidDataException($"unknown cache source '{text}' in the cache file"),
    };

    /// <summary>The stronger of two sources (see <see cref="CacheSource"/>).</summary>
    public static CacheSource Stronger(CacheSource a, CacheSource b) => (int)a >= (int)b ? a : b;
}

/// <summary>What a negative-cache ("not_found") row is about.</summary>
public enum NotFoundKind
{
    Player,
    Org,
}

/// <summary>A player as stored in the cache.</summary>
/// <param name="Handle">As RSI shows it.</param>
/// <param name="Orgs">Main org first, then affiliations in RSI page order. Only the main org has attributes here.</param>
/// <param name="Source">Why the entry exists.</param>
/// <param name="LastRefreshed">When the player page was last fetched (UTC).</param>
public sealed record CachedPlayer(
    string Handle,
    IReadOnlyList<OrgMembership> Orgs,
    CacheSource Source,
    DateTimeOffset LastRefreshed);

/// <summary>An org as stored in the cache. The member list and the attributes are refreshed separately.</summary>
/// <param name="AttributesRefreshed">When the attribute tags were fetched; null if never (org-list entries start without).</param>
/// <param name="MembersRefreshed">When the member list was fetched; null if never (hotkey entries have none).</param>
/// <param name="LastRefreshed">When this row was last written.</param>
public sealed record CachedOrg(
    string Sid,
    string? LongName,
    int? MemberCount,
    string? Archetype,
    string? Commitment,
    bool IsExclusive,
    IReadOnlyList<string> Activities,
    DateTimeOffset? AttributesRefreshed,
    DateTimeOffset? MembersRefreshed,
    CacheSource Source,
    DateTimeOffset LastRefreshed)
{
    public bool HasAttributes => AttributesRefreshed is not null;
}
