using Iris.Core.Rsi;

namespace Iris.Core.Config;

/// <summary>
/// The values an org-attribute rule may use: archetype and activity rules take one word from the RSI vocabulary
/// (<see cref="OrgVocabulary"/>); redacted and no-org rules take none. The config window and the importer share this rule.
/// </summary>
public static class OrgRuleValues
{
    /// <summary>True for the kinds that need a value (archetype and activity).</summary>
    public static bool NeedsValue(AttributeKind kind) => kind is AttributeKind.Archetype or AttributeKind.Activity;

    /// <summary>The known values of a kind (empty for kinds without a value).</summary>
    public static IReadOnlyList<string> VocabularyFor(AttributeKind kind) => kind switch
    {
        AttributeKind.Archetype => OrgVocabulary.Archetypes,
        AttributeKind.Activity => OrgVocabulary.Activities,
        _ => Array.Empty<string>(),
    };

    /// <summary>The vocabulary spelling of <paramref name="value"/> (compared ignoring upper/lower case), or null when it is not a known value.</summary>
    public static string? Canonical(AttributeKind kind, string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        foreach (var known in VocabularyFor(kind))
            if (string.Equals(known, text, StringComparison.OrdinalIgnoreCase))
                return known;
        return null;
    }

    /// <summary>
    /// One plain-words problem per rule whose value is missing or unknown; empty when every rule is fine.
    /// Rules are numbered from 1 in the order they appear, not counting the permanent No Match rule (IRIS writes it last, and the Settings
    /// window does not number it), so "Org rule N" is the Nth rule of the file.
    /// </summary>
    public static List<string> Check(SocialConfig config)
    {
        var problems = new List<string>();
        int number = 0;
        foreach (var rule in config.OrgRules)
        {
            if (rule.Kind == AttributeKind.NoMatch)
                continue; // not numbered
            number++;
            if (!NeedsValue(rule.Kind))
                continue;

            var kindName = rule.Kind == AttributeKind.Archetype ? "archetype" : "activity";
            if (string.IsNullOrWhiteSpace(rule.Value))
                problems.Add($"Org rule {number}: the {kindName} rule needs a value.");
            else if (Canonical(rule.Kind, rule.Value) is null)
                problems.Add($"Org rule {number}: \"{rule.Value}\" is not a known {kindName} value. Known values: "
                    + string.Join(", ", VocabularyFor(rule.Kind)) + ".");
        }
        return problems;
    }
}
