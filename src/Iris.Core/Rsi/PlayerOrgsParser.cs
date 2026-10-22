using AngleSharp.Dom;
using Iris.Core.Social;

namespace Iris.Core.Rsi;

/// <summary>
/// Reads a player's "organizations" page (https://robertsspaceindustries.com/en/citizens/HANDLE/organizations)
/// from saved HTML. Pure: HTML text in, <see cref="PlayerOrgs"/> out.
///
/// What the page holds (from the requirements): a "Main organization" block with the org's name, SID and
/// attributes, then one "Affiliation" block per extra org (name, SID, rank, member count; no attributes).
/// A hidden org is a block that has its heading but no name and no SID. The result keeps the page order
/// and marks the hidden ones with a null SID.
/// </summary>
public static class PlayerOrgsParser
{
    // ======================================================================================
    // PAGE LAYOUT. Every CSS selector and label string used by this parser is here.
    // Checked against real pages saved on 2026-10-21 (Nitebird, Bqnzai, Love2Hate):
    //   <div class="box-content org main visibility-V">           main org, visible
    //   <div class="box-content org affiliation  visibility-R ">  affiliation, hidden (text blanked out)
    //     <div class="title">Main organization</div> / "Affiliation"
    //     <p class="entry"><span class="label">Spectrum Identification (SID)</span><strong class="value">HEKATE</strong></p>
    //     main block only: labels "Archetype", "Prim. Activity", "Sec. Activity", "Commitment", ...
    // ======================================================================================

    /// <summary>Block classes: which kind of org, and whether it is hidden ("R" = redacted, "H" treated the same).</summary>
    private const string MainClass = "main";
    private const string AffiliationClass = "affiliation";
    private static readonly string[] HiddenClasses = { "visibility-R", "visibility-H" };

    /// <summary>Labels of the main org's activity rows ("Prim. Activity", "Sec. Activity"), "contains" match.</summary>
    private const string ActivityLabel = "Activity";

    /// <summary>Something that proves this is a citizen profile page (used only when no org block is found).</summary>
    private const string ProfileMarkerSelector = ".profile, .profile-content";

    /// <summary>One block per organization (main or affiliation, hidden or not).</summary>
    private const string OrgBlockSelector = ".box-content.org";

    /// <summary>The block's heading ("Main organization" / "Affiliation").</summary>
    private const string BlockHeadingSelector = ".title, .heading, h3";

    /// <summary>A label/value row inside a block, and its two parts.</summary>
    private const string EntrySelector = ".entry";
    private const string LabelSelector = ".label";
    private const string ValueSelector = ".value";

    /// <summary>A link to the org's own page; its last path segment is the SID.</summary>
    private const string OrgLinkSelector = "a[href*='/orgs/']";

    /// <summary>Label texts (matched case-insensitively, "contains").</summary>
    private const string HandleLabel = "Handle name";
    private const string SidLabel = "Spectrum Identification";
    private const string ArchetypeLabel = "Archetype";

    /// <summary>Heading texts, lower case, matched with "contains".</summary>
    private const string MainHeading = "main organization";
    private const string AffiliationHeading = "affiliation";

    /// <summary>Text shown inside a block whose org is hidden.</summary>
    private const string RedactedText = "redacted";

    /// <summary>Text shown inside a block when the player simply has no org there (lower case, "contains").</summary>
    private static readonly string[] NoOrgMarkers = { "no main org", "no affiliation", "public records" };

    /// <param name="html">The organizations page HTML.</param>
    /// <param name="requestedHandle">The handle we asked for (used when the page does not show one).</param>
    public static RsiResult<PlayerOrgs> Parse(string html, string requestedHandle)
    {
        var doc = HtmlText.Parse(html);

        var orgs = new List<OrgMembership>();
        int blocksSeen = 0;
        foreach (var block in doc.QuerySelectorAll(OrgBlockSelector))
        {
            // The block's classes say which kind it is; the visible title is the fallback.
            var heading = HtmlText.Of(block.QuerySelector(BlockHeadingSelector)).ToLowerInvariant();
            bool isMain = block.ClassList.Contains(MainClass) || heading.Contains(MainHeading, StringComparison.Ordinal);
            bool isAffiliation = !isMain && (block.ClassList.Contains(AffiliationClass)
                                             || heading.Contains(AffiliationHeading, StringComparison.Ordinal));
            if (!isMain && !isAffiliation)
                continue;

            blocksSeen++;
            var membership = ReadBlock(block, isMain);
            if (membership is not null)
                orgs.Add(membership);
        }

        if (blocksSeen == 0)
        {
            // No org blocks. That is fine for a profile that really shows no orgs, but not if the page
            // mentions the headings (then our block selector is probably wrong) or is not a profile at all.
            var pageText = HtmlText.Clean(doc.Body?.TextContent).ToLowerInvariant();
            bool mentionsHeadings = pageText.Contains(MainHeading, StringComparison.Ordinal)
                                    || pageText.Contains(AffiliationHeading, StringComparison.Ordinal);
            bool looksLikeProfile = doc.QuerySelector(ProfileMarkerSelector) is not null;
            if (mentionsHeadings || !looksLikeProfile)
                return RsiResult<PlayerOrgs>.Failed("player page: no organization blocks recognized (page layout may have changed)");
        }

        var shown = doc.DocumentElement is { } root ? ValueForLabel(root, HandleLabel) : null;
        var handle = RsiNames.IsValidHandle(shown) ? shown! : requestedHandle;

        return RsiResult<PlayerOrgs>.Found(new PlayerOrgs(handle, orgs));
    }

    /// <summary>Turns one block into a membership. Null means "this block shows no org at all".</summary>
    private static OrgMembership? ReadBlock(IElement block, bool isMain)
    {
        // A hidden org: RSI marks the block and blanks out its text.
        if (HiddenClasses.Any(c => block.ClassList.Contains(c)))
            return new OrgMembership(null, isMain);

        var sid = SidFromLabel(block) ?? SidFromLink(block);

        if (sid is null)
        {
            var text = HtmlText.Of(block).ToLowerInvariant();
            bool redacted = text.Contains(RedactedText, StringComparison.Ordinal);
            if (!redacted && NoOrgMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
                return null;

            // heading but no name and no SID: a hidden org
            return new OrgMembership(null, isMain);
        }

        if (!isMain)
            return new OrgMembership(sid, false);

        // main org: its attributes are labeled rows on today's page ("Archetype", "Prim. Activity",
        // "Sec. Activity"). For each part that has no labeled row, fall back to scanning the block for known words.
        var labeled = ValueForLabel(block, ArchetypeLabel);
        var labeledActivities = ValuesForLabel(block, ActivityLabel);
        var scan = OrgAttributeScanner.Scan(block);
        var archetype = labeled is null ? scan.Archetype : (OrgVocabulary.MatchArchetype(labeled) ?? labeled);
        IReadOnlyList<string> activities = labeledActivities.Count > 0 ? labeledActivities : scan.Activities;
        return new OrgMembership(sid, true, archetype, activities);
    }

    private static string? SidFromLabel(IElement block)
    {
        var value = ValueForLabel(block, SidLabel);
        return RsiNames.IsValidSid(value) ? value : null;
    }

    private static string? SidFromLink(IElement block)
    {
        var href = block.QuerySelector(OrgLinkSelector)?.GetAttribute("href");
        if (href is null)
            return null;
        var path = href.Split('?', '#')[0].TrimEnd('/');
        var segment = path.Substring(path.LastIndexOf('/') + 1);
        return RsiNames.IsValidSid(segment) ? segment : null;
    }

    /// <summary>All non-empty values of rows whose label contains <paramref name="label"/>, in page order, without duplicates.</summary>
    private static List<string> ValuesForLabel(IElement scope, string label)
    {
        var values = new List<string>();
        foreach (var entry in scope.QuerySelectorAll(EntrySelector))
        {
            if (!HtmlText.Of(entry.QuerySelector(LabelSelector)).Contains(label, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = HtmlText.Of(entry.QuerySelector(ValueSelector));
            if (value.Length > 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }
        return values;
    }

    /// <summary>Finds the row whose label contains <paramref name="label"/> and returns its value text (null if none or empty).</summary>
    private static string? ValueForLabel(IElement scope, string label)
    {
        foreach (var entry in scope.QuerySelectorAll(EntrySelector))
        {
            var labelText = HtmlText.Of(entry.QuerySelector(LabelSelector));
            if (!labelText.Contains(label, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = HtmlText.Of(entry.QuerySelector(ValueSelector));
            if (value.Length > 0)
                return value;
        }
        return null;
    }
}
