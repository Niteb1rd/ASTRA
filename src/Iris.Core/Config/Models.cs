namespace Iris.Core.Config;

/// <summary>The 8 overlay colors offered in the config menu (no white or black).</summary>
public enum OverlayColor
{
    Red,
    Orange,
    Yellow,
    Green,
    Cyan,
    Blue,
    Magenta,
    Purple,
}

/// <summary>What an org-attribute rule looks at.</summary>
public enum AttributeKind
{
    /// <summary>Org archetype tag, e.g. PMC, Syndicate, Corporation, Organization, Faith, Club.</summary>
    Archetype,

    /// <summary>Org focus activity tag, e.g. Exploration, Trading, Piracy.</summary>
    Activity,

    /// <summary>The player has at least one hidden (redacted) org.</summary>
    Redacted,

    /// <summary>The player belongs to no org at all.</summary>
    NoOrg,
}

/// <summary>A row of the player list.</summary>
public sealed record PlayerEntry(string Handle, OverlayColor Color);

/// <summary>A row of the org list. Priority 1 is highest.</summary>
public sealed record OrgEntry(string Sid, int Priority, OverlayColor Color);

/// <summary>A row of the org-attribute rule list. <see cref="Value"/> is null for Redacted and NoOrg.</summary>
public sealed record OrgAttributeRule(AttributeKind Kind, string? Value, int Priority, OverlayColor Color);

/// <summary>Everything in a shareable Social Overlay config file.</summary>
public sealed class SocialConfig
{
    /// <summary>Optional human-readable name for the shared list.</summary>
    public string? Name { get; set; }

    public List<PlayerEntry> Players { get; } = new();
    public List<OrgEntry> Orgs { get; } = new();
    public List<OrgAttributeRule> OrgRules { get; } = new();
}
