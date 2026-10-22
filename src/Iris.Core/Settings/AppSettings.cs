namespace Iris.Core.Settings;

/// <summary>
/// The settings of this PC (file <c>Data\settings.toml</c>). Separate from the list file on purpose: these are never
/// shared or exported. A record, so "did anything change?" is a plain <c>==</c>, and a changed copy is <c>settings with { ... }</c>.
/// </summary>
public sealed record AppSettings
{
    public const int FormatVersion = 1;

    /// <summary>How old a cached RSI entry may get before it is refreshed: 30 (default) or 7 days.</summary>
    public int CacheTtlDays { get; init; } = 30;

    /// <summary>Write the daily log file in Data\logs. The in-memory log always works.</summary>
    public bool SessionLogEnabled { get; init; } = true;

    /// <summary>Check GitHub for new IRIS versions. Only a setting for now; the check itself comes later.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>The user's own web address of a shared config file (https only), or empty for none.</summary>
    public string HostedConfigUrl { get; init; } = string.Empty;

    /// <summary>SHA-256 (lower-case hex) of the last hosted file that was applied or declined, or empty.</summary>
    public string HostedConfigLastHash { get; init; } = string.Empty;
}
