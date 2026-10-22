namespace Iris.Core.Config;

/// <summary>The one rule for the address of a hosted config file: absolute, https, no user name or password.</summary>
public static class HostedConfigUrl
{
    /// <summary>Null when <paramref name="text"/> is an acceptable hosted-config address; otherwise the reason, in plain words.</summary>
    public static string? Check(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return "Enter a web address that starts with https://";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return "That is not a valid web address. It must start with https://";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "Only https:// addresses are allowed.";
        if (uri.Host.Length == 0)
            return "That web address has no site name.";
        if (uri.UserInfo.Length > 0)
            return "Addresses with a user name or password are not allowed.";
        return null;
    }
}
