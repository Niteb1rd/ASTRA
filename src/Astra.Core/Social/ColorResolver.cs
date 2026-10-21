using Astra.Core.Config;

namespace Astra.Core.Social;

/// <summary>One org a player belongs to, as known from the cache.</summary>
/// <param name="Sid">Org SID, e.g. "J0K3R5". Null when the org is hidden (redacted).</param>
/// <param name="IsMain">True for the player's main org.</param>
/// <param name="Archetype">Org archetype tag if known (only fetched for hotkey lookups), else null.</param>
/// <param name="Activities">Org focus-activity tags if known, else empty.</param>
public sealed record OrgMembership(string? Sid, bool IsMain, string? Archetype = null, IReadOnlyList<string>? Activities = null)
{
    public bool IsRedacted => Sid is null;
    public IReadOnlyList<string> ActivityList => Activities ?? Array.Empty<string>();
}

/// <summary>Why a nameplate got its color. Decides the bracket shape.</summary>
public enum MatchKind
{
    PlayerList,     // { }
    OrgList,        // [ ]
    OrgAttribute,   // < >
}

/// <summary>The color decision for one player.</summary>
/// <param name="DecidingOrgSid">Org that caused an OrgList or OrgAttribute match (null for redacted / no-org rules).</param>
/// <param name="DecidingAttribute">For attribute matches: the attribute text shown in the panel, e.g. "PMC", "REDACTED", "No Org".</param>
public sealed record ColorMatch(MatchKind Kind, OverlayColor Color, string? DecidingOrgSid, string? DecidingAttribute)
{
    public string OpenBracket => Kind switch { MatchKind.PlayerList => "{", MatchKind.OrgList => "[", _ => "<" };
    public string CloseBracket => Kind switch { MatchKind.PlayerList => "}", MatchKind.OrgList => "]", _ => ">" };
}

/// <summary>
/// Applies the requirement's color rules, in order:
/// 1. player list (no tiebreak needed);
/// 2. org list: lowest priority number wins, ties go to the org listed first;
/// 3. org-attribute rules: lowest priority number wins, ties go to the rule listed first;
/// 4. nothing matches: no bracket (null).
/// All of the player's orgs count, main and affiliates.
/// </summary>
public static class ColorResolver
{
    public static ColorMatch? Resolve(string handle, IReadOnlyList<OrgMembership> orgs, SocialConfig config)
    {
        // 1. player list
        var player = config.Players.FirstOrDefault(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));
        if (player is not null)
            return new ColorMatch(MatchKind.PlayerList, player.Color, null, null);

        // 2. org list: config order is the final tiebreak, so scan rows in order and keep strictly better priority
        OrgEntry? bestOrg = null;
        foreach (var row in config.Orgs)
        {
            bool member = orgs.Any(o => o.Sid is not null && string.Equals(o.Sid, row.Sid, StringComparison.OrdinalIgnoreCase));
            if (member && (bestOrg is null || row.Priority < bestOrg.Priority))
                bestOrg = row;
        }
        if (bestOrg is not null)
            return new ColorMatch(MatchKind.OrgList, bestOrg.Color, bestOrg.Sid, null);

        // 3. org-attribute rules
        OrgAttributeRule? bestRule = null;
        string? ruleOrg = null;
        string? ruleText = null;
        foreach (var rule in config.OrgRules)
        {
            var (hit, sid, text) = RuleHits(rule, orgs);
            if (hit && (bestRule is null || rule.Priority < bestRule.Priority))
            {
                bestRule = rule;
                ruleOrg = sid;
                ruleText = text;
            }
        }
        if (bestRule is not null)
            return new ColorMatch(MatchKind.OrgAttribute, bestRule.Color, ruleOrg, ruleText);

        return null;
    }

    private static (bool hit, string? sid, string? text) RuleHits(OrgAttributeRule rule, IReadOnlyList<OrgMembership> orgs)
    {
        switch (rule.Kind)
        {
            case AttributeKind.NoOrg:
                return (orgs.Count == 0, null, "No Org");
            case AttributeKind.Redacted:
                return (orgs.Any(o => o.IsRedacted), null, "REDACTED");
            case AttributeKind.Archetype:
                // main org first, then affiliates in listed order
                foreach (var o in OrderMainFirst(orgs))
                    if (o.Archetype is not null && string.Equals(o.Archetype, rule.Value, StringComparison.OrdinalIgnoreCase))
                        return (true, o.Sid, rule.Value);
                return (false, null, null);
            case AttributeKind.Activity:
                foreach (var o in OrderMainFirst(orgs))
                    if (o.ActivityList.Any(a => string.Equals(a, rule.Value, StringComparison.OrdinalIgnoreCase)))
                        return (true, o.Sid, rule.Value);
                return (false, null, null);
            default:
                return (false, null, null);
        }
    }

    private static IEnumerable<OrgMembership> OrderMainFirst(IReadOnlyList<OrgMembership> orgs) =>
        orgs.Where(o => o.IsMain).Concat(orgs.Where(o => !o.IsMain));
}
