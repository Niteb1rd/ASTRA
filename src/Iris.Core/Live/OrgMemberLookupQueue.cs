using System.Data.Common;
using Iris.Core.Cache;
using Iris.Core.Config;
using Iris.Core.Rsi;

namespace Iris.Core.Live;

/// <summary>
/// Looks up the primary page of players who are cached members of a listed org, one at a time, in the background. A member of an org on the user's org
/// list is in the cache as a name only (the org's member list); until their player page is read their orgs are unknown, so the overlay would show them as No Match
/// with "Orgs: not looked up" instead of in the org's color. When the detection loop confirms such a name it calls <see cref="Consider"/>; a name that is a cached member of
/// a listed org, has no cached player page and no recent "not found" note is queued ONCE. The lookup is <see cref="CacheRefresher.LookupOrgListMemberAsync"/>
/// (cache first, the player's primary page only, stored with source <c>org_list_member</c>), which goes through the app's single rate-limited RSI client, so the
/// requests line up behind everything else at one per second at best. The queue holds at most <see cref="Capacity"/> names; others are considered again later.
/// Thread safe: <see cref="Consider"/> is called by the detection thread, <see cref="RunAsync"/> runs on its own task.
/// </summary>
public sealed class OrgMemberLookupQueue
{
    /// <summary>At most this many names wait; a longer line is not queued (the names are asked again at the next read).</summary>
    public const int Capacity = 32;

    /// <summary>A name that was looked at and not queued (or whose lookup failed) is not looked at again for this long (milliseconds).</summary>
    public const double RecheckMs = 60_000.0;

    private const int MaxRemembered = 512;

    private readonly CacheDatabase _database;
    private readonly Func<string, CancellationToken, Task<PlayerLookup>> _lookup;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _checkedAtMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);

    public OrgMemberLookupQueue(CacheDatabase database, Func<string, CancellationToken, Task<PlayerLookup>> lookup, IClock clock, ILog? log = null)
    {
        _database = database;
        _lookup = lookup;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>The queue for the app: lookups go through <paramref name="refresher"/>.</summary>
    public static OrgMemberLookupQueue For(CacheDatabase database, CacheRefresher refresher, IClock clock, ILog? log = null) =>
        new(database, (handle, ct) => refresher.LookupOrgListMemberAsync(handle, ct), clock, log);

    /// <summary>How many names wait.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>
    /// Queues <paramref name="handle"/> when it is a cached member of an org on <paramref name="config"/>'s org list and nothing is known about the player yet.
    /// True when it was queued. Cheap for names that were looked at lately (a dictionary check). <paramref name="nowMs"/> is the loop's monotonic clock.
    /// </summary>
    public bool Consider(string handle, SocialConfig config, double nowMs)
    {
        if (config.Orgs.Count == 0 || !RsiNames.IsValidHandle(handle))
            return false;
        lock (_gate)
        {
            if (_requested.Contains(handle) || _pending.Count >= Capacity)
                return false;
            if (_checkedAtMs.TryGetValue(handle, out var at) && nowMs - at < RecheckMs)
                return false;
        }

        // the cache is asked outside the lock (its own lock, and a few small queries)
        bool wanted;
        try
        {
            wanted = _database.GetPlayer(handle) is null
                     && !_database.IsKnownNotFound(handle, _clock.UtcNow)
                     && config.Orgs.Any(org => _database.IsOrgMember(org.Sid, handle));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            return false; // a cache problem must not stop the detection loop
        }

        lock (_gate)
        {
            if (_checkedAtMs.Count >= MaxRemembered)
                _checkedAtMs.Clear();
            _checkedAtMs[handle] = nowMs;
            if (!wanted || _pending.Count >= Capacity || !_requested.Add(handle))
                return false;
            _pending.Enqueue(handle);
        }

        _signal.Release();
        return true;
    }

    /// <summary>
    /// Looks up the next waiting name. False when nothing waits. A failed lookup (no network, RSI busy) is forgotten, so the name may be queued again after
    /// <see cref="RecheckMs"/>; a found or not-found answer is in the cache and the name is not asked again. Cancellation passes through.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        string? handle;
        lock (_gate)
        {
            if (!_pending.TryDequeue(out handle))
                return false;
        }

        try
        {
            var result = await _lookup(handle, ct).ConfigureAwait(false);
            if (result.Status == PlayerLookupStatus.Failed)
            {
                lock (_gate)
                    _requested.Remove(handle);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                _requested.Remove(handle);
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
                _requested.Remove(handle);
            _log.Write(_clock.UtcNow, $"live: lookup of org member {handle} failed: {ex.Message}");
        }

        return true;
    }

    /// <summary>The worker: looks up the queued names one by one until <paramref name="ct"/> is cancelled. Run it on its own task.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                await ProcessNextAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the app is quitting
        }
    }

    /// <summary>Forgets the names that wait (detection stopped: nothing new is wanted). A lookup that is running finishes.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var handle in _pending)
                _requested.Remove(handle);
            _pending.Clear();
        }
    }
}
