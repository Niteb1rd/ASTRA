using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Config;
using Iris.Core.Rsi;

namespace Iris.App.ViewModels;

/// <summary>One entry of the "Type" drop-down of the Org-Attribute rules.</summary>
public sealed record KindChoice(AttributeKind Kind, string Name);

/// <summary>The fixed lists behind the drop-downs. XAML reads them with x:Static.</summary>
public static class Choices
{
    public const int DefaultPriority = 5;

    /// <summary>The 8 overlay colors.</summary>
    public static IReadOnlyList<OverlayColor> Colors { get; } = Enum.GetValues<OverlayColor>();

    /// <summary>1 (highest) to 9.</summary>
    public static IReadOnlyList<int> Priorities { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    public static IReadOnlyList<KindChoice> Kinds { get; } = new[]
    {
        new KindChoice(AttributeKind.Archetype, "Org-Archetype"),
        new KindChoice(AttributeKind.Activity, "Org-Activity"),
        new KindChoice(AttributeKind.Redacted, "Redacted"),
        new KindChoice(AttributeKind.NoOrg, "No Org"),
    };

    /// <summary>Suggested values for a rule of this kind (empty for kinds without a value). Read from the RSI vocabulary.</summary>
    public static IReadOnlyList<string> SuggestionsFor(AttributeKind kind) => OrgRuleValues.VocabularyFor(kind);

    /// <summary>The name shown to the user for a kind (the stored value and TOML text are not affected).</summary>
    public static string DisplayName(AttributeKind kind) =>
        Kinds.FirstOrDefault(k => k.Kind == kind)?.Name ?? kind.ToString();

    /// <summary>Text shown (disabled) in the Value cell for kinds that take no value.</summary>
    public static string InfoTextFor(AttributeKind kind) => kind switch
    {
        AttributeKind.Redacted => "Player belongs in a Redacted org",
        AttributeKind.NoOrg => "Player is in no orgs/hidden orgs",
        _ => string.Empty,
    };

    /// <summary>True for the kinds that need a value (Archetype and Activity).</summary>
    public static bool KindNeedsValue(AttributeKind kind) => OrgRuleValues.NeedsValue(kind);
}
