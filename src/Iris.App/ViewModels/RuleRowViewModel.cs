using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Config;

namespace Iris.App.ViewModels;

/// <summary>
/// A row of the Org-Attribute tab. Rules are not checked against RSI.
/// The permanent No Match row (<see cref="IsNoMatch"/>) is always the last one: its type, value and priority (10) are fixed and shown disabled,
/// only its color can be changed, and it cannot be removed.
/// </summary>
public sealed class RuleRowViewModel : ObservableObject
{
    private AttributeKind _kind;
    private string _value;
    private int _priority;
    private OverlayColor _color;

    public RuleRowViewModel(AttributeKind kind, string? value, int priority, OverlayColor color)
    {
        _kind = kind;
        _priority = priority;
        _color = color;
        NeedsValue = Choices.KindNeedsValue(kind);
        InfoText = Choices.InfoTextFor(kind);

        // A loaded value that matches the vocabulary apart from upper/lower case is normalized to the vocabulary spelling.
        // One that does not match at all is kept (and shown as invalid) so it is not silently lost.
        var text = value ?? string.Empty;
        var vocabulary = Choices.SuggestionsFor(kind);
        var known = vocabulary.FirstOrDefault(v => string.Equals(v, text.Trim(), StringComparison.OrdinalIgnoreCase));
        _value = NeedsValue ? (known ?? text) : string.Empty;
        Suggestions = BuildSuggestions(vocabulary, _value);
    }

    /// <summary>
    /// Org-Archetype, Org-Activity, Redacted or No Org. Changing it clears a value that is not in the new
    /// type's list (a value that fitted one type, say "PMC", means nothing for another).
    /// </summary>
    public AttributeKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value))
                return;
            var vocabulary = Choices.SuggestionsFor(value);
            NeedsValue = Choices.KindNeedsValue(value);
            InfoText = Choices.InfoTextFor(value);
            if (!NeedsValue || !vocabulary.Contains(_value))
                _value = string.Empty;
            Suggestions = BuildSuggestions(vocabulary, _value);
            Raise(nameof(IsNoMatch));
            Raise(nameof(KindName));
            Raise(nameof(NeedsValue));
            Raise(nameof(InfoText));
            Raise(nameof(Suggestions));
            Raise(nameof(Value));
            Raise(nameof(HasInvalidValue));
        }
    }

    /// <summary>
    /// The chosen archetype or activity. Empty for Redacted and No Org (the explanation text shown in the cell is
    /// <see cref="InfoText"/> and is never stored). Null from the drop-down (its list was swapped) counts as empty.
    /// </summary>
    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value ?? string.Empty))
                Raise(nameof(HasInvalidValue));
        }
    }

    public int Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    public OverlayColor Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    /// <summary>True for the permanent No Match row.</summary>
    public bool IsNoMatch => _kind == AttributeKind.NoMatch;

    /// <summary>The type as shown to the user ("Org-Archetype", "No Match"...). Display only.</summary>
    public string KindName => Choices.DisplayName(_kind);

    /// <summary>False for Redacted, No Org and No Match: the Value cell only shows <see cref="InfoText"/>.</summary>
    public bool NeedsValue { get; private set; }

    /// <summary>Explanation for the disabled Value cell of Redacted, No Org and No Match rows. Display only.</summary>
    public string InfoText { get; private set; } = string.Empty;

    /// <summary>The values the drop-down offers: the vocabulary of the type, plus a loaded unknown value so it stays visible.</summary>
    public IReadOnlyList<string> Suggestions { get; private set; }

    /// <summary>True when the type needs a value and the current one is not in its vocabulary (empty counts as missing, not unknown).</summary>
    public bool HasInvalidValue =>
        NeedsValue && _value.Length > 0 && !Choices.SuggestionsFor(_kind).Contains(_value);

    /// <summary>True when the row can be saved as far as its value goes.</summary>
    public bool IsValueAcceptable => !NeedsValue || (_value.Length > 0 && !HasInvalidValue);

    private static IReadOnlyList<string> BuildSuggestions(IReadOnlyList<string> vocabulary, string current)
    {
        if (current.Length == 0 || vocabulary.Contains(current))
            return vocabulary;
        var list = new List<string>(vocabulary) { current };
        return list;
    }
}
