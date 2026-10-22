using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Iris.Core.Rsi;

/// <summary>
/// Reads an org's public page (https://robertsspaceindustries.com/en/orgs/SID) from saved HTML.
/// Pure: HTML text in, <see cref="OrgInfo"/> out. No network, so it can be tested with saved pages.
///
/// What it expects (from the requirements): a heading like "Jokers Gambit / J0K3R5", a member count
/// like "104 members", and tags: archetype (PMC...), commitment (Casual/Regular/Hardcore), "Exclusive",
/// and focus activities (Freelancing...).
/// </summary>
public static class OrgPageParser
{
    // ======================================================================================
    // PAGE LAYOUT ASSUMPTIONS. Every CSS selector and label string used by this parser is here.
    // They are educated guesses and MUST be checked against the captured fixtures
    // (tests/Iris.Core.Tests/Fixtures/rsi/org-J0K3R5.html, see tools/Iris.CaptureFixtures).
    // ======================================================================================

    /// <summary>Where to look for the page heading, best guess first. The first heading with " / " in it wins.</summary>
    private static readonly string[] HeadingSelectors = { "#organization h1", "h1" };

    /// <summary>Text between the long name and the SID in the heading.</summary>
    private const string HeadingSeparator = " / ";

    /// <summary>The page area that holds the tags and the focus icons. If absent the whole page is used.</summary>
    private const string OrgAreaSelector = "#organization";

    /// <summary>Elements that carry one tag each (archetype, commitment, Exclusive, activities).</summary>
    private const string TagSelector = ".tags li, .tags span, .tags a, .focus li, .focus img, .focus span";

    /// <summary>Elements that may hold the member count text ("104 members").</summary>
    private const string MemberCountSelector = ".count, .logo .count";

    private static readonly Regex MemberCountPattern =
        new(@"(\d[\d,]*)\s+members?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="html">The org page HTML.</param>
    /// <param name="requestedSid">The SID we asked for (used when the heading's SID differs in more than letter case).</param>
    public static RsiResult<OrgInfo> Parse(string html, string requestedSid)
    {
        var doc = HtmlText.Parse(html);

        // --- heading: "Long Name / SID" ---
        string? heading = null;
        foreach (var selector in HeadingSelectors)
        {
            foreach (var h in doc.QuerySelectorAll(selector))
            {
                var text = HtmlText.Of(h);
                if (text.Contains(HeadingSeparator, StringComparison.Ordinal))
                {
                    heading = text;
                    break;
                }
            }
            if (heading is not null)
                break;
        }
        if (heading is null)
            return RsiResult<OrgInfo>.Failed("org page: heading 'Name / SID' not found (page layout may have changed)");

        int cut = heading.LastIndexOf(HeadingSeparator, StringComparison.Ordinal);
        var longName = heading.Substring(0, cut).Trim();
        var symbol = heading.Substring(cut + HeadingSeparator.Length).Trim();
        if (longName.Length == 0)
            return RsiResult<OrgInfo>.Failed("org page: heading has no org name");
        var sid = string.Equals(symbol, requestedSid, StringComparison.OrdinalIgnoreCase) ? symbol : requestedSid;

        // --- member count ---
        int? memberCount = null;
        foreach (var el in doc.QuerySelectorAll(MemberCountSelector))
        {
            memberCount = ReadMemberCount(HtmlText.Of(el));
            if (memberCount is not null)
                break;
        }
        if (memberCount is null)
            memberCount = ReadMemberCount(HtmlText.Clean(doc.Body?.TextContent));

        // --- tags ---
        string? archetype = null;
        string? commitment = null;
        bool exclusive = false;
        var activities = new List<string>();
        bool anyTag = false;

        foreach (var el in doc.QuerySelectorAll(TagSelector))
        {
            foreach (var text in TagTexts(el))
                anyTag |= Classify(text, ref archetype, ref commitment, ref exclusive, activities);
        }

        if (!anyTag)
        {
            // fallback: scan everything inside the org area for known attribute words
            IElement? area = doc.QuerySelector(OrgAreaSelector) ?? doc.Body;
            if (area is not null)
            {
                var scan = OrgAttributeScanner.Scan(area);
                archetype = scan.Archetype;
                commitment = scan.Commitment;
                exclusive = scan.IsExclusive;
                activities.AddRange(scan.Activities);
            }
        }

        return RsiResult<OrgInfo>.Found(new OrgInfo(sid, longName, memberCount, archetype, commitment, exclusive, activities));
    }

    private static int? ReadMemberCount(string text)
    {
        var m = MemberCountPattern.Match(text);
        if (!m.Success)
            return null;
        var digits = m.Groups[1].Value.Replace(",", "");
        return int.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>The words a tag element may carry: its own text, plus alt/title for icon tags.</summary>
    private static IEnumerable<string> TagTexts(IElement el)
    {
        var text = HtmlText.Of(el);
        if (text.Length > 0 && text.Length <= 40)
            yield return text;
        foreach (var attribute in new[] { "alt", "title" })
        {
            var value = HtmlText.Clean(el.GetAttribute(attribute));
            if (value.Length > 0 && value.Length <= 40)
                yield return value;
        }
    }

    /// <summary>Files one tag text into the right slot. Returns true when the text was a known attribute word.</summary>
    private static bool Classify(string text, ref string? archetype, ref string? commitment, ref bool exclusive, List<string> activities)
    {
        var a = OrgVocabulary.MatchArchetype(text);
        if (a is not null)
        {
            archetype ??= a;
            return true;
        }

        var c = OrgVocabulary.MatchCommitment(text);
        if (c is not null)
        {
            commitment ??= c;
            return true;
        }

        if (OrgVocabulary.IsExclusive(text))
        {
            exclusive = true;
            return true;
        }

        var act = OrgVocabulary.MatchActivity(text);
        if (act is not null)
        {
            if (!activities.Contains(act, StringComparer.OrdinalIgnoreCase))
                activities.Add(act);
            return true;
        }

        return false;
    }
}
