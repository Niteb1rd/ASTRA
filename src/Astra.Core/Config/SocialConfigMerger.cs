namespace Astra.Core.Config;

/// <summary>The merged list plus what the merge did.</summary>
public sealed class MergeResult
{
    public MergeResult(SocialConfig config)
    {
        Config = config;
    }

    /// <summary>The merged config: the original's rows first, then the imported rows that were new.</summary>
    public SocialConfig Config { get; }

    public int AddedPlayers { get; internal set; }
    public int AddedOrgs { get; internal set; }
    public int AddedRules { get; internal set; }

    public int SkippedPlayers { get; internal set; }
    public int SkippedOrgs { get; internal set; }
    public int SkippedRules { get; internal set; }

    public int AddedTotal => AddedPlayers + AddedOrgs + AddedRules;

    /// <summary>Imported rows that were left out because the original already had them.</summary>
    public int SkippedDuplicates => SkippedPlayers + SkippedOrgs + SkippedRules;
}

/// <summary>
/// Combines two configs. The ORIGINAL always wins: its rows keep their order, color and priority, and an imported row
/// is only added when the original has no row for the same thing.
/// Same thing means: a player with the same handle, an org with the same SID, a rule with the same kind and value
/// (kinds without a value, Redacted and NoOrg, are the same by kind alone). Upper/lower case does not matter.
/// The result keeps the original's list name.
/// </summary>
public static class SocialConfigMerger
{
    public static MergeResult Merge(SocialConfig original, SocialConfig imported)
    {
        var merged = new SocialConfig { Name = original.Name };
        var result = new MergeResult(merged);

        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in original.Players)
        {
            merged.Players.Add(p);
            handles.Add(p.Handle);
        }
        foreach (var p in imported.Players)
        {
            if (handles.Add(p.Handle))
            {
                merged.Players.Add(p);
                result.AddedPlayers++;
            }
            else
            {
                result.SkippedPlayers++;
            }
        }

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in original.Orgs)
        {
            merged.Orgs.Add(o);
            sids.Add(o.Sid);
        }
        foreach (var o in imported.Orgs)
        {
            if (sids.Add(o.Sid))
            {
                merged.Orgs.Add(o);
                result.AddedOrgs++;
            }
            else
            {
                result.SkippedOrgs++;
            }
        }

        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in original.OrgRules)
        {
            merged.OrgRules.Add(r);
            rules.Add(RuleKey(r));
        }
        foreach (var r in imported.OrgRules)
        {
            if (rules.Add(RuleKey(r)))
            {
                merged.OrgRules.Add(r);
                result.AddedRules++;
            }
            else
            {
                result.SkippedRules++;
            }
        }

        return result;
    }

    /// <summary>"kind|value" for rules with a value, just "kind|" for Redacted and NoOrg. Compared ignoring case.</summary>
    private static string RuleKey(OrgAttributeRule rule)
    {
        var hasValue = rule.Kind is AttributeKind.Archetype or AttributeKind.Activity;
        return rule.Kind + "|" + (hasValue ? (rule.Value ?? string.Empty).Trim() : string.Empty);
    }
}
