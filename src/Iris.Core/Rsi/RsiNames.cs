using System.Text.RegularExpressions;

namespace Iris.Core.Rsi;

/// <summary>
/// Checks handles and SIDs before they are put into a web address.
/// This is the gate that keeps odd text (spaces, slashes, "..", newlines) out of URLs.
/// </summary>
public static class RsiNames
{
    /// <summary>Shortest player handle IRIS accepts. Shorter text on a nameplate is treated as noise.</summary>
    public const int MinHandleLength = 4;

    /// <summary>Longest player handle IRIS accepts.</summary>
    public const int MaxHandleLength = 25;

    // \z (not $) on purpose: "$" would also accept a trailing newline.
    private static readonly Regex HandlePattern = new(
        "^[A-Za-z0-9_-]{" + MinHandleLength + "," + MaxHandleLength + "}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex SidPattern = new(@"^[A-Za-z0-9_-]{1,20}\z", RegexOptions.CultureInvariant);

    /// <summary>Player handle: letters, digits, "_" and "-", 4 to 25 characters.</summary>
    public static bool IsValidHandle(string? handle) => handle is not null && HandlePattern.IsMatch(handle);

    /// <summary>Org SID: letters, digits, "_" and "-", 1 to 20 characters.</summary>
    public static bool IsValidSid(string? sid) => sid is not null && SidPattern.IsMatch(sid);
}

/// <summary>
/// Every web address IRIS uses, built in one place. Network policy: only robertsspaceindustries.com over https.
/// </summary>
public static class RsiUrls
{
    public const string Host = "robertsspaceindustries.com";
    public const string BaseUrl = "https://robertsspaceindustries.com";

    /// <summary>RSI's members API (POST, JSON body).</summary>
    public const string OrgMembersApi = BaseUrl + "/api/orgs/getOrgMembers";

    /// <summary>A player's organizations page. Throws ArgumentException for an invalid handle.</summary>
    public static string PlayerOrgs(string handle)
    {
        if (!RsiNames.IsValidHandle(handle))
            throw new ArgumentException("invalid name", nameof(handle));
        return $"{BaseUrl}/en/citizens/{handle}/organizations";
    }

    /// <summary>An org's public page. Throws ArgumentException for an invalid SID.</summary>
    public static string OrgPage(string sid)
    {
        if (!RsiNames.IsValidSid(sid))
            throw new ArgumentException("invalid name", nameof(sid));
        return $"{BaseUrl}/en/orgs/{sid}";
    }

    /// <summary>True only for https://robertsspaceindustries.com/... on the normal port, with no user-info trick.</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);
}
