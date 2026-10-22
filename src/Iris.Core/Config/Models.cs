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

    /// <summary>
    /// The permanent last rule: a detected player who matches nothing else (not the player list, not the org list, no other
    /// rule). Every config has exactly one (see <see cref="NoMatchRule"/>). Always priority 10, never has a value.
    /// </summary>
    NoMatch,
}

/// <summary>A row of the player list.</summary>
public sealed record PlayerEntry(string Handle, OverlayColor Color);

/// <summary>A row of the org list. Priority 1 is highest.</summary>
public sealed record OrgEntry(string Sid, int Priority, OverlayColor Color);

/// <summary>A row of the org-attribute rule list. <see cref="Value"/> is null for Redacted, NoOrg and NoMatch.</summary>
public sealed record OrgAttributeRule(AttributeKind Kind, string? Value, int Priority, OverlayColor Color);

/// <summary>Everything in a shareable Social Overlay config file.</summary>
public sealed class SocialConfig
{
    /// <summary>Optional human-readable name for the shared list.</summary>
    public string? Name { get; set; }

    public List<PlayerEntry> Players { get; } = new();
    public List<OrgEntry> Orgs { get; } = new();
    public List<OrgAttributeRule> OrgRules { get; } = new();

    /// <summary>Number of org-attribute rules the user manages: all of them except the permanent No Match rule.</summary>
    public int UserRuleCount => OrgRules.Count(r => r.Kind != AttributeKind.NoMatch);
}

/// <summary>
/// The permanent "No Match" org-attribute rule. Every config has exactly one, always LAST, always priority 10 (the other rules use 1 to 9,
/// so it can never win against a real match) and never a value; only its color can be edited. It gives a detected player who matches
/// nothing else a color and the attribute bracket &lt; &gt;, so the user sees which players the overlay detected.
/// </summary>
public static class NoMatchRule
{
    public const int Priority = 10;

    public const OverlayColor DefaultColor = OverlayColor.Blue;

    /// <summary>The rule a config gets when it has none: Blue, priority 10.</summary>
    public static OrgAttributeRule CreateDefault() => new(AttributeKind.NoMatch, null, Priority, DefaultColor);

    /// <summary>The (first) No Match rule of <paramref name="config"/>, or null when it has none.</summary>
    public static OrgAttributeRule? Find(SocialConfig config) =>
        config.OrgRules.FirstOrDefault(r => r.Kind == AttributeKind.NoMatch);

    /// <summary>
    /// Makes sure <paramref name="config"/> has exactly one No Match rule, as its last rule, with priority 10 and no value:
    /// the first one is kept (and fixed up), any others are dropped, and the default is added when there is none. Silent.
    /// </summary>
    public static void Ensure(SocialConfig config)
    {
        var keep = Find(config) ?? CreateDefault();
        config.OrgRules.RemoveAll(r => r.Kind == AttributeKind.NoMatch);
        config.OrgRules.Add(keep with { Value = null, Priority = Priority });
    }

    /// <summary>A copy of <paramref name="config"/> that satisfies <see cref="Ensure"/>. The original is not changed.</summary>
    public static SocialConfig Normalized(SocialConfig config)
    {
        var copy = new SocialConfig { Name = config.Name };
        copy.Players.AddRange(config.Players);
        copy.Orgs.AddRange(config.Orgs);
        copy.OrgRules.AddRange(config.OrgRules);
        Ensure(copy);
        return copy;
    }
}
