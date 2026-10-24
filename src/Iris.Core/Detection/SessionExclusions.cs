namespace Iris.Core.Detection;

/// <summary>
/// The names that were excluded for this run of IRIS by the DUPLICATE RULE: a name read on 2 or more separate WHITE nameplates at once is not a player (a HUD line, a
/// reflection, a sign), see <see cref="NameplateTracker"/>. The list lives in memory only: it is NOT written to the permanent exclusion list
/// (<see cref="ExclusionList"/>, which holds the names the user and the shipped defaults put there), it survives everything the live engine resets (the engine forgets its tracks
/// when the game window loses the focus; this list stays) and it is lost when IRIS closes. A name on one of the user's lists is never added.
/// An excluded name gets no bracket, no panel entry and no RSI lookup. Names are compared ignoring upper and lower case. Thread safe.
/// </summary>
public sealed class SessionExclusions
{
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the name is excluded for this session.</summary>
    public bool Contains(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        lock (_gate)
            return _names.Contains(name.Trim());
    }

    /// <summary>Excludes the name for this session. True when it was not excluded before (the caller logs it once then).</summary>
    public bool TryAdd(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        lock (_gate)
            return _names.Add(name.Trim());
    }

    /// <summary>How many names are excluded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _names.Count;
        }
    }

    /// <summary>A copy of the excluded names (no particular order).</summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_gate)
                return _names.ToArray();
        }
    }
}
