using Iris.Core.Cache;
using Iris.Core.Rsi;

namespace Iris.Core.Config;

/// <summary>How a checked row turned out.</summary>
public enum RowCheckState
{
    /// <summary>The name exists on RSI (or is known to, from a fresh cache entry).</summary>
    Valid,

    /// <summary>RSI has no such citizen / org (a definite answer, remembered in the cache).</summary>
    NotFound,

    /// <summary>The text cannot be a handle / SID, or it is already in the list. Not looked up.</summary>
    InvalidFormat,

    /// <summary>RSI could not be asked or answered badly (offline, timeout...). Says nothing about the name; not cached.</summary>
    Failed,
}

/// <summary>The result of checking one row of the config window.</summary>
/// <param name="State">The outcome.</param>
/// <param name="Message">Plain words for the user. For Valid it repeats <paramref name="DisplayText"/>.</param>
/// <param name="DisplayText">Valid only: how RSI writes the handle, or the org's long name.</param>
public sealed record RowCheckResult(RowCheckState State, string Message, string? DisplayText = null)
{
    public bool IsValid => State == RowCheckState.Valid;
}

/// <summary>
/// Checks one player handle or org SID typed into the config window. No UI code in here.
/// Order of steps: (1) format, (2) duplicate in the list, (3) the cache (fresh known name: valid without a request;
/// remembered 404: not found without a request), (4) one RSI request, whose answer is stored in the cache
/// with source player_list / org_list.
/// For an org only the org page is requested (long name and tags); its member list is the refresher's job.
/// A failed request is reported but never stored, so a bad moment cannot make a real name look missing.
/// </summary>
public sealed class RowValidator
{
    public const string InvalidHandleMessage = "Handles are 4 to 64 characters: letters, digits, - and _ (not starting or ending with -)";
    public const string InvalidSidMessage = "SIDs are 1 to 20 characters: letters, digits, - and _";
    public const string DuplicateMessage = "already in the list";
    public const string FallbackFailureMessage = "RSI not reachable";

    private readonly CacheDatabase _database;
    private readonly IRsiClient _rsi;
    private readonly IClock _clock;
    private readonly CacheSettings _settings;
    private readonly ILog _log;

    public RowValidator(CacheDatabase database, IRsiClient rsi, IClock clock, CacheSettings settings, ILog? log = null)
    {
        _database = database;
        _rsi = rsi;
        _clock = clock;
        _settings = settings;
        _log = log ?? NullLog.Instance;
    }

    // ---------------------------------------------------------------------------------------
    // Checks that need no cache and no network (the window also uses these when saving)
    // ---------------------------------------------------------------------------------------

    /// <summary>Null when the handle looks right; otherwise the InvalidFormat result.</summary>
    public static RowCheckResult? CheckHandleFormat(string? handle) =>
        RsiNames.IsValidHandle(handle?.Trim()) ? null : new RowCheckResult(RowCheckState.InvalidFormat, InvalidHandleMessage);

    /// <summary>Null when the SID looks right; otherwise the InvalidFormat result.</summary>
    public static RowCheckResult? CheckSidFormat(string? sid) =>
        RsiNames.IsValidSid(sid?.Trim()) ? null : new RowCheckResult(RowCheckState.InvalidFormat, InvalidSidMessage);

    /// <summary>True if <paramref name="value"/> equals one of <paramref name="others"/> (ignoring case and surrounding spaces).</summary>
    public static bool IsDuplicate(string value, IEnumerable<string> others)
    {
        var trimmed = value.Trim();
        return others.Any(o => string.Equals(o?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------
    // Players
    // ---------------------------------------------------------------------------------------

    /// <param name="handle">What the user typed.</param>
    /// <param name="otherHandles">The handles in the other rows of the list (for the duplicate check).</param>
    public async Task<RowCheckResult> ValidatePlayerAsync(
        string handle, IEnumerable<string> otherHandles, CancellationToken ct = default)
    {
        var name = (handle ?? string.Empty).Trim();
        var format = CheckHandleFormat(name);
        if (format is not null)
            return format;
        if (IsDuplicate(name, otherHandles))
            return new RowCheckResult(RowCheckState.InvalidFormat, DuplicateMessage);

        var now = _clock.UtcNow;

        var cached = _database.GetPlayer(name);
        if (cached is not null && !IsStale(cached.LastRefreshed, now))
        {
            // Same data, but now it is on the user's list: promote the source, keep the original refresh time.
            if (CacheSources.Stronger(cached.Source, CacheSource.PlayerList) != cached.Source)
                _database.SavePlayer(new PlayerOrgs(cached.Handle, cached.Orgs), CacheSource.PlayerList, cached.LastRefreshed);
            return Valid(cached.Handle);
        }

        if (_database.IsKnownNotFound(name, now))
            return NotFound();

        var result = await _rsi.GetPlayerOrgsAsync(name, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                _database.SavePlayer(result.Data!, CacheSource.PlayerList, _clock.UtcNow);
                return Valid(result.Data!.Handle);
            case RsiStatus.NotFound:
                _database.SaveNotFound(NotFoundKind.Player, name, CacheSource.PlayerList, _clock.UtcNow);
                return NotFound();
            default:
                return Failed("player " + name, result.Reason);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Orgs
    // ---------------------------------------------------------------------------------------

    /// <param name="sid">What the user typed.</param>
    /// <param name="otherSids">The SIDs in the other rows of the list (for the duplicate check).</param>
    public async Task<RowCheckResult> ValidateOrgAsync(
        string sid, IEnumerable<string> otherSids, CancellationToken ct = default)
    {
        var name = (sid ?? string.Empty).Trim();
        var format = CheckSidFormat(name);
        if (format is not null)
            return format;
        if (IsDuplicate(name, otherSids))
            return new RowCheckResult(RowCheckState.InvalidFormat, DuplicateMessage);

        var now = _clock.UtcNow;

        var cached = _database.GetOrg(name);
        if (cached?.LongName is { } cachedName && !IsStale(cached.LastRefreshed, now))
        {
            // Same data, but now it is on the user's org list: promote the source, keep the refresh times.
            if (CacheSources.Stronger(cached.Source, CacheSource.OrgList) != cached.Source)
                _database.PromoteOrgSource(cached.Sid, CacheSource.OrgList);
            return Valid(cachedName);
        }

        if (_database.IsKnownOrgNotFound(name, now))
            return NotFound();

        var result = await _rsi.GetOrgInfoAsync(name, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case RsiStatus.Found:
                _database.SaveOrgAttributes(result.Data!, CacheSource.OrgList, _clock.UtcNow);
                return Valid(result.Data!.LongName);
            case RsiStatus.NotFound:
                _database.SaveNotFound(NotFoundKind.Org, name, CacheSource.OrgList, _clock.UtcNow);
                return NotFound();
            default:
                return Failed("org " + name, result.Reason);
        }
    }

    // ---------------------------------------------------------------------------------------

    private bool IsStale(DateTimeOffset lastRefreshed, DateTimeOffset now) => now - lastRefreshed > _settings.Ttl;

    private static RowCheckResult Valid(string displayText) =>
        new(RowCheckState.Valid, displayText, displayText);

    private static RowCheckResult NotFound() =>
        new(RowCheckState.NotFound, "Not found on RSI");

    private RowCheckResult Failed(string what, string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? FallbackFailureMessage : reason!;
        _log.Write(_clock.UtcNow, $"Config check of {what} failed: {text}");
        return new RowCheckResult(RowCheckState.Failed, text);
    }
}
