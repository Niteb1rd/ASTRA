using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Iris.Core.Rsi;

/// <summary>Small shared helpers for the HTML parsers.</summary>
internal static class HtmlText
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Parses a whole page or a fragment of HTML (AngleSharp is forgiving about both).</summary>
    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>Collapses runs of whitespace (including non-breaking spaces) to one space and trims.</summary>
    public static string Clean(string? text) => text is null ? "" : Whitespace.Replace(text, " ").Trim();

    /// <summary>The cleaned text of an element, or "" when the element is null.</summary>
    public static string Of(IElement? element) => element is null ? "" : Clean(element.TextContent);
}

/// <summary>
/// The words RSI uses for org attributes. Used to recognise a tag by its text, so the parsers
/// do not depend on exact CSS class names. Case-insensitive; results use the canonical spelling below.
/// </summary>
public static class OrgVocabulary
{
    public static readonly IReadOnlyList<string> Archetypes =
        new[] { "PMC", "Syndicate", "Corporation", "Organization", "Faith", "Club" };

    public static readonly IReadOnlyList<string> Commitments =
        new[] { "Casual", "Regular", "Hardcore" };

    /// <summary>Focus activities known at the time of writing. Check against captured fixtures.</summary>
    public static readonly IReadOnlyList<string> Activities = new[]
    {
        "Security", "Resources", "Exploration", "Smuggling", "Piracy", "Social", "Freelancing",
        "Trading", "Transport", "Scouting", "Medical", "Engineering", "Bounty Hunting", "Data Running",
    };

    public const string Exclusive = "Exclusive";

    public static string? MatchArchetype(string text) => Match(Archetypes, text);

    public static string? MatchCommitment(string text) => Match(Commitments, text);

    public static string? MatchActivity(string text) => Match(Activities, text);

    public static bool IsExclusive(string text) => string.Equals(text, Exclusive, StringComparison.OrdinalIgnoreCase);

    private static string? Match(IReadOnlyList<string> words, string text)
    {
        foreach (var word in words)
            if (string.Equals(word, text, StringComparison.OrdinalIgnoreCase))
                return word;
        return null;
    }
}

/// <summary>
/// Structure-agnostic attribute reader: looks at every small piece of text (and every alt/title attribute)
/// inside a container and keeps the ones that are known attribute words. Links are skipped so an org
/// whose NAME happens to be "Trading" is not mistaken for a tag.
/// Used as the main reader for the player page's main-org block and as a fallback for the org page.
/// </summary>
internal static class OrgAttributeScanner
{
    public sealed record Result(
        string? Archetype,
        string? Commitment,
        bool IsExclusive,
        IReadOnlyList<string> Activities);

    private const int MaxTagLength = 40;

    public static Result Scan(IElement scope)
    {
        string? archetype = null;
        string? commitment = null;
        bool exclusive = false;
        var activities = new List<string>();

        foreach (var element in scope.QuerySelectorAll("*"))
        {
            if (IsInsideLink(element, scope))
                continue;

            foreach (var text in Candidates(element))
            {
                if (archetype is null)
                    archetype = OrgVocabulary.MatchArchetype(text);
                if (commitment is null)
                    commitment = OrgVocabulary.MatchCommitment(text);
                if (OrgVocabulary.IsExclusive(text))
                    exclusive = true;
                var activity = OrgVocabulary.MatchActivity(text);
                if (activity is not null && !activities.Contains(activity, StringComparer.OrdinalIgnoreCase))
                    activities.Add(activity);
            }
        }

        return new Result(archetype, commitment, exclusive, activities);
    }

    private static IEnumerable<string> Candidates(IElement element)
    {
        // text of leaf elements (no child elements) ...
        if (element.Children.Length == 0)
        {
            var text = HtmlText.Of(element);
            if (text.Length > 0 && text.Length <= MaxTagLength)
                yield return text;
        }

        // ... and icon-style tags whose words live in alt / title attributes
        foreach (var attribute in new[] { "alt", "title" })
        {
            var value = HtmlText.Clean(element.GetAttribute(attribute));
            if (value.Length > 0 && value.Length <= MaxTagLength)
                yield return value;
        }
    }

    private static bool IsInsideLink(IElement element, IElement scope)
    {
        for (IElement? e = element; e is not null && !ReferenceEquals(e, scope); e = e.ParentElement)
        {
            if (string.Equals(e.LocalName, "a", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
