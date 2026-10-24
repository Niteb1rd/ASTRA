namespace Iris.Core.Detection;

/// <summary>Rules about the shape of a name read from a nameplate, beyond the characters it may have.</summary>
public static class NameShape
{
    /// <summary>
    /// True when the name does not start or end with <c>-</c> (the handles RSI hands out today do not; an underscore may start and end a
    /// name). This is a rule for reads from the screen only: <c>RsiNames.IsValidHandle</c> (RSI pages, member lists, typed handles) does not apply it,
    /// because an older account might have such a handle. A read such as <c>-PIL0TFISH</c> is an animation stage or a misread, not a name: it is no vote for the tracker (the nameplate itself
    /// still is one). An empty name has no valid edges.
    /// </summary>
    public static bool HasValidEdges(string? name) =>
        !string.IsNullOrEmpty(name) && name[0] != '-' && name[^1] != '-';
}
