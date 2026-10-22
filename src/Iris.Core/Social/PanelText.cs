namespace Iris.Core.Social;

/// <summary>
/// Builds the right-panel "Orgs:" line from the requirements:
/// - SIDs (short names), comma separated;
/// - the org that caused the match goes first; an attribute match adds "(attribute)"; a No Match result marks no org;
/// - otherwise RSI page order (main org first);
/// - the main org ends with "*"; hidden orgs show as REDACTED (REDACTED* for a hidden main org);
/// - no orgs: "Orgs: none found";
/// - at most 40 characters including "Orgs: "; if longer, cut after the last org that fits and add ", etc."
/// </summary>
public static class PanelText
{
    public const int MaxLineLength = 40;
    private const string Prefix = "Orgs: ";
    private const string Etc = ", etc.";

    public static string OrgLine(IReadOnlyList<OrgMembership> orgs, ColorMatch? match)
    {
        if (orgs.Count == 0)
            return "Orgs: none found";

        // RSI page order, main first
        var ordered = orgs.Where(o => o.IsMain).Concat(orgs.Where(o => !o.IsMain)).ToList();

        // deciding org first (for a REDACTED rule match: the first hidden org)
        int i = -1;
        if (match?.DecidingOrgSid is { } sid)
            i = ordered.FindIndex(o => o.Sid is not null && string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));
        else if (match is { Kind: MatchKind.OrgAttribute, DecidingAttribute: "REDACTED" })
            i = ordered.FindIndex(o => o.IsRedacted);
        if (i > 0)
        {
            var first = ordered[i];
            ordered.RemoveAt(i);
            ordered.Insert(0, first);
        }

        var items = new List<string>(ordered.Count);
        foreach (var o in ordered)
        {
            var text = (o.Sid ?? "REDACTED") + (o.IsMain ? "*" : "");
            if (match is { Kind: MatchKind.OrgAttribute, DecidingOrgSid: { } dsid, DecidingAttribute: { } attr }
                && o.Sid is not null && string.Equals(o.Sid, dsid, StringComparison.OrdinalIgnoreCase))
                text += $" ({attr})";
            items.Add(text);
        }

        var full = Prefix + string.Join(", ", items);
        if (full.Length <= MaxLineLength)
            return full;

        for (int k = items.Count - 1; k >= 1; k--)
        {
            var cut = Prefix + string.Join(", ", items.Take(k)) + Etc;
            if (cut.Length <= MaxLineLength)
                return cut;
        }
        // even one org plus ", etc." is too long: hard-cut the first org's text
        var room = MaxLineLength - Prefix.Length - Etc.Length;
        return Prefix + items[0][..Math.Max(1, room)] + Etc;
    }
}
