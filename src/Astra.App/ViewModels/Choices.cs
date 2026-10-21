using System;
using System.Collections.Generic;
using Astra.Core.Config;
using Astra.Core.Rsi;

namespace Astra.App.ViewModels;

/// <summary>One entry of the "Kind" drop-down of the org rules.</summary>
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
        new KindChoice(AttributeKind.Archetype, "Archetype"),
        new KindChoice(AttributeKind.Activity, "Activity"),
        new KindChoice(AttributeKind.Redacted, "Redacted"),
        new KindChoice(AttributeKind.NoOrg, "No Org"),
    };

    /// <summary>Suggested values for a rule of this kind (empty for kinds without a value). Read from the RSI vocabulary.</summary>
    public static IReadOnlyList<string> SuggestionsFor(AttributeKind kind) => kind switch
    {
        AttributeKind.Archetype => OrgVocabulary.Archetypes,
        AttributeKind.Activity => OrgVocabulary.Activities,
        _ => Array.Empty<string>(),
    };

    /// <summary>True for the kinds that need a value (Archetype and Activity).</summary>
    public static bool KindNeedsValue(AttributeKind kind) => kind is AttributeKind.Archetype or AttributeKind.Activity;
}
