using Iris.Core.Config;
using Iris.Core.Rsi;

namespace Iris.Core.Detection;

/// <summary>
/// Decides whether text read from a nameplate is worth looking at.
/// </summary>
public static class NameFilter
{
    /// <summary>
    /// True if the text should be ignored: it is not a valid handle (4 to 25 letters, digits, - or _),
    /// or it is on the exclusion list or has the shape of a contact code (<see cref="ContactCode"/>, "PA-5968-XF"),
    /// AND is not on the user's own player list.
    /// The user's player list wins: a name the user typed in is never ignored just because it is excluded or code-shaped.
    /// Names are compared with the accuracy look-alikes of <see cref="LookAlikes"/> (0/O, 1/I) and nothing else.
    /// Org-list members and hotkey lookups have no such override; they simply follow the exclusion list.
    /// </summary>
    public static bool ShouldIgnore(string handle, ExclusionList exclusions, SocialConfig config)
    {
        if (!RsiNames.IsValidHandle(handle))
            return true;
        if (!exclusions.IsExcluded(handle) && !ContactCode.IsContactCode(handle))
            return false;
        return !IsOnPlayerList(handle, config);
    }

    /// <summary>True if the handle is on the user's player list (case-insensitive, tolerating the look-alikes 0/O and 1/I).</summary>
    public static bool IsOnPlayerList(string handle, SocialConfig config) =>
        config.Players.Any(p => LookAlikes.NamesEqual(p.Handle, handle));
}
