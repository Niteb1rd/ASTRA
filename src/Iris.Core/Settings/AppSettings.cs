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

    /// <summary>
    /// Opt-in: look at the hosted config address in the background every 24 hours and offer the update when the file changed
    /// (see <see cref="Iris.Core.Config.HostedConfigSchedule"/>). Off by default. Only meaningful with a valid <see cref="HostedConfigUrl"/>.
    /// </summary>
    public bool HostedConfigAutoCheck { get; init; }

    /// <summary>When the last automatic check was made (UTC, whole seconds; success or failure), or null if there was none yet.</summary>
    public DateTimeOffset? HostedConfigLastCheck { get; init; }

    // ---- Overlay panel (Phase 2) ----

    public const int PanelWidthPercentMin = 8;
    public const int PanelWidthPercentMax = 40;
    public const int PanelTextSizeMin = 10;
    public const int PanelTextSizeMax = 40;
    public const int PanelRightGapPercentMin = 0;
    public const int PanelRightGapPercentMax = 40;
    public const int PanelTopGapPercentMin = 0;
    public const int PanelTopGapPercentMax = 60;

    /// <summary>Show the right-hand panel (one box per bracketed player). The brackets themselves are always shown.</summary>
    public bool PanelEnabled { get; init; } = true;

    /// <summary>Width of the panel as a percentage of the game window width (8 to 40).</summary>
    public int PanelWidthPercent { get; init; } = 16;

    /// <summary>Panel text size in pixels on a 1440p-high game window (10 to 40); it scales linearly with the real window height.</summary>
    public int PanelTextSize { get; init; } = 18;

    /// <summary>Space between the panel's right edge and the right edge of the screen, as a percentage of the width (0 to 40).</summary>
    public int PanelRightGapPercent { get; init; } = 10;

    /// <summary>Space between the top of the screen and the panel, as a percentage of the height (0 to 60).</summary>
    public int PanelTopGapPercent { get; init; } = 15;
}
