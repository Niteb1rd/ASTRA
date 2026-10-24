namespace Iris.Core.Detection;

/// <summary>
/// The names that were excluded for this run of IRIS by the DUPLICATE RULE: a name read on 2 or more separate WHITE nameplates at once is not a player (a HUD line, a
/// reflection, a sign), see <see cref="NameplateTracker"/>. The list lives in memory only: it is NOT written to the permanent exclusion list
/// (<see cref="ExclusionList"/>, which holds the names the user and the shipped defaults put there), it survives everything the live engine resets (the engine forgets its tracks
/// when the game window loses the focus; this list stays) and it is lost when IRIS closes. A name on one of the user's lists is never added.
/// An excluded name gets no bracket, no panel entry and no RSI lookup. Names are compared ignoring upper and lower case, and, only when the caller asks for it
/// (<see cref="NameplateTrackerSettings.ExclusionFoldsO0"/>), with the digit 0 and the letter O taken as the same character (<see cref="LookAlikes.FoldO0"/>). Thread safe.
/// </summary>
public sealed class SessionExclusions
{
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    // The same names with every 0 turned into O, kept up to date always so that the folded comparison can be switched on at any time.
    private readonly HashSet<string> _folded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the name is excluded for this session. <paramref name="foldO0"/>: compare with 0 and O taken as the same character.</summary>
    public bool Contains(string? name, bool foldO0 = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        lock (_gate)
            return foldO0 ? _folded.Contains(LookAlikes.FoldO0(trimmed)) : _names.Contains(trimmed);
    }

    /// <summary>
    /// Excludes the name for this session. True when it was not excluded before (the caller logs it once then); with <paramref name="foldO0"/> a name that only differs from an
    /// excluded one in 0 against O counts as excluded before (it is still remembered as written).
    /// </summary>
    public bool TryAdd(string name, bool foldO0 = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        lock (_gate)
        {
            var added = _names.Add(trimmed);
            var addedFolded = _folded.Add(LookAlikes.FoldO0(trimmed));
            return foldO0 ? addedFolded : added;
        }
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
