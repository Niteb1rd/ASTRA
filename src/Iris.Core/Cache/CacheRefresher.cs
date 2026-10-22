using Iris.Core.Config;
using Iris.Core.Rsi;
using Iris.Core.Social;

namespace Iris.Core.Cache;

/// <summary>Outcome of looking up one player.</summary>
public enum PlayerLookupStatus
{
    Found,

    /// <summary>RSI says there is no such citizen (a recent answer, possibly from the negative cache).</summary>
    NotFound,

    /// <summary>We could not find out (timeout, network error, bad name, ...) and have nothing cached. Nothing was negative-cached.</summary>
    Failed,
}

/// <summary>The answer to "who is this player and which orgs are they in".</summary>
public sealed class PlayerLookup
{
    private PlayerLookup(PlayerLookupStatus status, string handle, IReadOnlyList<OrgMembership> orgs, string? reason, bool fromCache, bool isStale)
    {
        Status = status;
        Handle = handle;
        Orgs = orgs;
        Reason = reason;
        FromCache = fromCache;
        IsStale = isStale;
    }

    public PlayerLookupStatus Status { get; }

    public string Handle { get; }

    /// <summary>Main org first. Empty unless Found.</summary>
    public IReadOnlyList<OrgMembership> Orgs { get; }

    /// <summary>Plain-words reason when Failed.</summary>
    public string? Reason { get; }

    /// <summary>True when no request to RSI was needed for the player page.</summary>
    public bool FromCache { get; }

    /// <summary>True when the data is older than the TTL because refreshing it failed (better than nothing).</summary>
    public bool IsStale { get; }

    public static PlayerLookup Found(string handle, IReadOnlyList<OrgMembership> orgs, bool fromCache, bool isStale) =>
        new(PlayerLookupStatus.Found, handle, orgs, null, fromCache, isStale);

    public static PlayerLookup NotFound(string handle, bool fromCache) =>
        new(PlayerLookupStatus.NotFound, handle, Array.Empty<OrgMembership>(), null, fromCache, false);

    public static PlayerLookup Failed(string handle, string reason) =>
        new(PlayerLookupStatus.Failed, handle, Array.Empty<OrgMembership>(), reason, false, false);
}

/// <summary>The entries of the user's lists that need (re)fetching.</summary>
/// <param name="Players">Handles from the player list.</param>
/// <param name="Orgs">SIDs from the org list.</param>
public sealed record StaleEntries(IReadOnlyList<string> Players, IReadOnlyList<string> Orgs)
{
    public bool IsEmpty => Players.Count == 0 && Orgs.Count == 0;
}

/// <summary>What a refresh run did. Failures are plain-words lines such as "player Nitebird: timeout after 15 s".</summary>
public sealed class RefreshSummary
{
    public int PlayersRefreshed { get; set; }
    public int PlayersNotFound { get; set; }
    public int OrgsRefreshed { get; set; }
    public int OrgsNotFound { get; set; }
    public List<string> Failures { get; } = new();

    /// <summary>How many list entries were asked of RSI (found, not found or failed). Zero means there was nothing to do.</summary>
    public int Attempted => PlayersRefreshed + PlayersNotFound + OrgsRefreshed + OrgsNotFound + Failures.Count;
}

/// <summary>
/// The rules that turn "what is cached" plus "what RSI says" into a cache. It decides what is stale,
/// refreshes through the <see cref="IRsiClient"/> (which does the rate limiting) and writes to the
/// <see cref="CacheDatabase"/>. The rules:
/// - An entry is stale when it is older than the TTL (30 or 7 days, see <see cref="CacheSettings"/>).
/// - The full refresh (<see cref="RefreshAllAsync"/>, the "Refresh Cache (long time)" button) refreshes ONLY the user's player list
///   and org list, whatever their age. It never touches 'hotkey' or 'org_list_member' entries.
/// - The fill-missing refresh (<see cref="RefreshMissingOrStaleAsync"/>, run in the background after a config was imported or
///   loaded) fetches only the entries of those same lists that have no cache entry or whose entry is stale. Orgs: org page and member list.
/// - Only a definite 404 is negative-cached (30 days). Timeouts and other failures cache nothing.
///   A name with a "not found" note is never refetched automatically; it is retried only when its nameplate is
///   encountered again after the note expired (hotkey / org-list-member lookups), or by the full refresh.
/// - Player-list caching stores only the player's primary page. Org-list caching stores only the org's long name
///   and member list. Neither follows links to other pages.
/// </summary>
public sealed class CacheRefresher
{
    private readonly CacheDatabase _database;
    private readonly IRsiClient _rsi;
    private readonly IClock _clock;
    private readonly CacheSettings _settings;
    private readonly ILog _log;

    public CacheRefresher(CacheDatabase database, IRsiClient rsi, IClock clock, CacheSettings settings, ILog? log = null)
    {
        _database = database;
        _rsi = rsi;
        _clock = clock;
        _settings = settings;
        _log = log ?? NullLog.Instance;
    }

    // ---------------------------------------------------------------------------------------
    // Lists: automatic and manual refresh
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Which entries of the user's lists are stale: missing from the cache, or older than the TTL.
    /// Names with a "not found" note are left out (they are not retried automatically).
    /// </summary>
    public StaleEntries FindStale(SocialConfig config)
    {
        var now = _clock.UtcNow;

        var players = new List<string>();
        foreach (var handle in ListedHandles(config))
        {
            if (_database.GetNotFoundTime(NotFoundKind.Player, handle) is not null)
                continue;
            var cached = _database.GetPlayer(handle);
            if (cached is null || IsStale(cached.LastRefreshed, now))
                players.Add(handle);
        }

        var orgs = new List<string>();
        foreach (var sid in ListedSids(config))
        {
            if (_database.GetNotFoundTime(NotFoundKind.Org, sid) is not null)
                continue;
            var cached = _database.GetOrg(sid);
            if (cached?.MembersRefreshed is not { } refreshed || IsStale(refreshed, now))
                orgs.Add(sid);
        }

        return new StaleEntries(players, orgs);
    }

    /// <summary>
    /// "Fill missing": fetches only the entries of the user's lists that have no cache entry or whose entry is older than the TTL
    /// (players: the player page; orgs: the org page and the member list). Fresh entries and names with a "not found" note are left alone.
    /// Stops with <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled; what was fetched until then is kept.
    /// </summary>
    public Task<RefreshSummary> RefreshMissingOrStaleAsync(SocialConfig config, CancellationToken ct = default)
    {
        var stale = FindStale(config);
        return RefreshListEntriesAsync(stale.Players, stale.Orgs, ct);
    }

    /// <summary>
    /// The full refresh ("Refresh Cache (long time)"): refetches every entry of the user's player list and org list,
    /// fresh or not (names with an old "not found" note are tried again too). Entries that came from
    /// hotkey lookups or org-list members and are not on the lists are not touched.
    /// Stops with <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task<RefreshSummary> RefreshAllAsync(SocialConfig config, CancellationToken ct = default) =>
        RefreshListEntriesAsync(ListedHandles(config).ToList(), ListedSids(config).ToList(), ct);

    private async Task<RefreshSummary> RefreshListEntriesAsync(IReadOnlyList<string> handles, IReadOnlyList<string> sids, CancellationToken ct)
    {
        var summary = new RefreshSummary();

        foreach (var handle in handles)
        {
            ct.ThrowIfCancellationRequested();
            var result = await FetchAndStorePlayerAsync(handle, CacheSource.PlayerList, ct).ConfigureAwait(false);
            switch (result.Status)
            {
                case RsiStatus.Found:
                    summary.PlayersRefreshed++;
                    break;
                case RsiStatus.NotFound:
                    summary.PlayersNotFound++;
                    break;
                default:
                    summary.Failures.Add($"player {handle}: {result.Reason}");
                    break;
            }
        }

        foreach (var sid in sids)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshOrgListEntryAsync(sid, summary, ct).ConfigureAwait(false);
        }

        return summary;
    }

    /// <summary>Org-list refresh for one org: its long name (org page) and its member list (members API). Nothing more.</summary>
    private async Task RefreshOrgListEntryAsync(string sid, RefreshSummary summary, CancellationToken ct)
    {
        var info = await _rsi.GetOrgInfoAsync(sid, ct).ConfigureAwait(false);
        if (info.Status == RsiStatus.NotFound)
        {
            _database.SaveNotFound(NotFoundKind.Org, sid, CacheSource.OrgList, _clock.UtcNow);
            summary.OrgsNotFound++;
            return;
        }
        if (info.Status != RsiStatus.Found)
        {
            Fail(summary, $"org {sid}: {info.Reason}");
            return;
        }

        var members = await _rsi.GetOrgMembersAsync(sid, ct).ConfigureAwait(false);
        if (members.Status != RsiStatus.Found)
        {
            // The org page exists, so a missing member list is a problem, not "no such org". Keep what we had.
            Fail(summary, $"org {sid} members: {members.Reason ?? "not found"}");
            return;
        }

        _database.SaveOrgListEntry(sid, info.Data!.LongName, members.Data!.Handles, _clock.UtcNow);
        summary.OrgsRefreshed++;
    }

    // ---------------------------------------------------------------------------------------
    // Nameplate lookups
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Hotkey path: the player's orgs, with attributes for every org we can get them for.
    /// Steps: (1) the player page, from cache when fresh, otherwise from RSI (stored with source 'hotkey');
    /// (2) for each affiliate org (the main org's attributes already came with the player page) the org page,
    /// from cache when fresh, otherwise from RSI (stored with source 'hotkey'). Hidden orgs have nothing to look up.
    /// If an org page cannot be fetched, that org is returned without attributes; the lookup still succeeds.
    /// </summary>
    public async Task<PlayerLookup> LookupPlayerForHotkeyAsync(string handle, CancellationToken ct = default)
    {
        var player = await GetPlayerCacheFirstAsync(handle, CacheSource.Hotkey, ct).ConfigureAwait(false);
        if (player.Status != PlayerLookupStatus.Found)
            return player;

        var orgs = new List<OrgMembership>(player.Orgs.Count);
        foreach (var membership in player.Orgs)
        {
            if (membership.IsMain || membership.Sid is null)
            {
                orgs.Add(membership);
                continue;
            }

            var org = await GetOrgAttributesAsync(membership.Sid, ct).ConfigureAwait(false);
            orgs.Add(org is null ? membership : membership with { Archetype = org.Archetype, Activities = org.Activities });
        }

        return PlayerLookup.Found(player.Handle, orgs, player.FromCache, player.IsStale);
    }

    /// <summary>
    /// Queues a lookup of one member of a listed org (their nameplate was seen). Only the player's primary page
    /// is fetched, never org pages; stored with source 'org_list_member'. Cache first, like the hotkey path.
    /// The actual waiting happens in the shared rate limiter, so calls simply line up.
    /// </summary>
    public Task<PlayerLookup> LookupOrgListMemberAsync(string handle, CancellationToken ct = default) =>
        GetPlayerCacheFirstAsync(handle, CacheSource.OrgListMember, ct);

    // ---------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------

    /// <summary>Cache first; asks RSI when there is nothing cached, the cache is stale, or an old "not found" note has expired.</summary>
    private async Task<PlayerLookup> GetPlayerCacheFirstAsync(string handle, CacheSource source, CancellationToken ct)
    {
        if (!RsiNames.IsValidHandle(handle))
            return PlayerLookup.Failed(handle, "invalid name");

        var now = _clock.UtcNow;
        if (_database.IsKnownNotFound(handle, now))
            return PlayerLookup.NotFound(handle, fromCache: true);

        var cached = _database.GetPlayer(handle);
        if (cached is not null && !IsStale(cached.LastRefreshed, now))
            return PlayerLookup.Found(cached.Handle, cached.Orgs, fromCache: true, isStale: false);

        var result = await FetchAndStorePlayerAsync(handle, source, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                var fresh = _database.GetPlayer(handle);
                return fresh is null
                    ? PlayerLookup.Failed(handle, "cache write did not stick")
                    : PlayerLookup.Found(fresh.Handle, fresh.Orgs, fromCache: false, isStale: false);

            case RsiStatus.NotFound:
                return PlayerLookup.NotFound(handle, fromCache: false);

            default:
                // could not refresh: old data beats no data
                if (cached is not null)
                    return PlayerLookup.Found(cached.Handle, cached.Orgs, fromCache: true, isStale: true);
                return PlayerLookup.Failed(handle, result.Reason ?? "unknown error");
        }
    }

    /// <summary>Asks RSI for the player page and stores the answer: data on Found, a "not found" note on 404, nothing on failure.</summary>
    private async Task<RsiResult<PlayerOrgs>> FetchAndStorePlayerAsync(string handle, CacheSource source, CancellationToken ct)
    {
        var result = await _rsi.GetPlayerOrgsAsync(handle, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        switch (result.Status)
        {
            case RsiStatus.Found:
                var data = result.Data!;
                // key the row by the name we asked for if RSI's spelling differs by more than letter case
                if (!string.Equals(data.Handle, handle, StringComparison.OrdinalIgnoreCase))
                    data = data with { Handle = handle };
                _database.SavePlayer(data, source, now);
                break;

            case RsiStatus.NotFound:
                _database.SaveNotFound(NotFoundKind.Player, handle, source, now);
                break;

            default:
                _log.Write(now, $"cache: player {handle} not refreshed: {result.Reason}");
                break;
        }

        return result;
    }

    /// <summary>
    /// The cached org with fresh attributes, fetching the org page when needed. Null when attributes
    /// are unavailable (404 or failure with nothing usable cached).
    /// </summary>
    private async Task<CachedOrg?> GetOrgAttributesAsync(string sid, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var cached = _database.GetOrg(sid);
        if (cached?.AttributesRefreshed is { } at && !IsStale(at, now))
            return cached;

        if (_database.IsKnownOrgNotFound(sid, now))
            return null;

        var result = await _rsi.GetOrgInfoAsync(sid, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                _database.SaveOrgAttributes(result.Data!, CacheSource.Hotkey, _clock.UtcNow);
                return _database.GetOrg(sid);

            case RsiStatus.NotFound:
                // Orgs on the user's org list are only changed by the list refresh, never by a hotkey lookup.
                if (cached?.Source != CacheSource.OrgList)
                    _database.SaveNotFound(NotFoundKind.Org, sid, CacheSource.Hotkey, _clock.UtcNow);
                return null;

            default:
                _log.Write(now, $"cache: org {sid} attributes not refreshed: {result.Reason}");
                return cached is { HasAttributes: true } ? cached : null;
        }
    }

    private void Fail(RefreshSummary summary, string line)
    {
        summary.Failures.Add(line);
        _log.Write(_clock.UtcNow, "cache: " + line);
    }

    private bool IsStale(DateTimeOffset lastRefreshed, DateTimeOffset now) => now - lastRefreshed > _settings.Ttl;

    private static IEnumerable<string> ListedHandles(SocialConfig config) =>
        config.Players.Select(p => p.Handle).Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ListedSids(SocialConfig config) =>
        config.Orgs.Select(o => o.Sid).Distinct(StringComparer.OrdinalIgnoreCase);
}
