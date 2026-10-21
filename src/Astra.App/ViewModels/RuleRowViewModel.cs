using System;
using System.Collections.Generic;
using Astra.Core.Config;

namespace Astra.App.ViewModels;

/// <summary>A row of the Org rules tab. Rules are not checked against RSI.</summary>
public sealed class RuleRowViewModel : ObservableObject
{
    private AttributeKind _kind;
    private string _value;
    private int _priority;
    private OverlayColor _color;

    public RuleRowViewModel(AttributeKind kind, string? value, int priority, OverlayColor color)
    {
        _kind = kind;
        _value = value ?? string.Empty;
        _priority = priority;
        _color = color;
        NeedsValue = Choices.KindNeedsValue(kind);
        Suggestions = Choices.SuggestionsFor(kind);
    }

    /// <summary>
    /// Archetype, Activity, Redacted or No Org. Changing it clears the value: a value that fitted one kind
    /// (say "PMC") means nothing for another, and the suggestion list changes with the kind.
    /// </summary>
    public AttributeKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value))
                return;
            NeedsValue = Choices.KindNeedsValue(value);
            Suggestions = Choices.SuggestionsFor(value);
            Raise(nameof(NeedsValue));
            Raise(nameof(Suggestions));
            Value = string.Empty;
        }
    }

    /// <summary>The archetype or activity text (editable, with suggestions). Ignored for Redacted and No Org.</summary>
    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
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

    /// <summary>False for Redacted and No Org: the Value box is disabled.</summary>
    public bool NeedsValue { get; private set; }

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();
}
