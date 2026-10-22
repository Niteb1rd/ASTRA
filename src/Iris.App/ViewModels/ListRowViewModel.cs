using System;
using Iris.Core.Config;
using Iris.Core.Rsi;

namespace Iris.App.ViewModels;

/// <summary>What the Status cell of a row shows.</summary>
public enum RowStatusKind
{
    /// <summary>Nothing typed yet.</summary>
    Empty,

    /// <summary>Typed (or loaded from the file) but not checked against RSI yet.</summary>
    Unchecked,

    /// <summary>A check is running.</summary>
    Checking,

    Valid,
    NotFound,

    /// <summary>Wrong format, or a duplicate.</summary>
    Invalid,

    /// <summary>RSI could not be asked.</summary>
    Failed,
}

/// <summary>
/// A row that holds a name which is checked against RSI (a player handle or an org SID) and has a color.
/// Handles the status display. The window starts the checks; this class only keeps the state.
/// </summary>
public abstract class ListRowViewModel : ObservableObject
{
    private string _value;
    private OverlayColor _color;
    private RowStatusKind _statusKind;
    private string _statusText = string.Empty;
    private string _note = string.Empty;

    protected ListRowViewModel(string value, OverlayColor color)
    {
        _value = value;
        _color = color;
        _statusKind = value.Trim().Length == 0 ? RowStatusKind.Empty : RowStatusKind.Unchecked;
    }

    /// <summary>The handle or SID as typed.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? string.Empty))
                return;
            Version++;
            IsFlaggedDuplicate = false;
            SetStatus(_value.Trim().Length == 0 ? RowStatusKind.Empty : RowStatusKind.Unchecked, string.Empty);
            OnValueEdited();
        }
    }

    public OverlayColor Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    /// <summary>Counts text edits. A check result is only used if the version is still the one the check started with.</summary>
    public int Version { get; private set; }

    /// <summary>True when the last result said "already in the list".</summary>
    public bool IsFlaggedDuplicate { get; private set; }

    public RowStatusKind StatusKind => _statusKind;

    public string StatusText => _statusText;

    public bool IsChecking => _statusKind == RowStatusKind.Checking;

    /// <summary>An informational line under the status (for example the exclusion-list hint). Empty = none.</summary>
    public string Note
    {
        get => _note;
        protected set
        {
            if (Set(ref _note, value))
                Raise(nameof(HasNote));
        }
    }

    public bool HasNote => _note.Length > 0;

    /// <summary>Called after the text was edited. Players use it to update the exclusion-list note.</summary>
    protected virtual void OnValueEdited()
    {
    }

    /// <summary>Shows the spinner.</summary>
    public void BeginCheck() => SetStatus(RowStatusKind.Checking, "Checking…");

    /// <summary>Forgets the last result (used to re-check a row whose "already in the list" may no longer be true).</summary>
    public void ResetStatus()
    {
        IsFlaggedDuplicate = false;
        SetStatus(_value.Trim().Length == 0 ? RowStatusKind.Empty : RowStatusKind.Unchecked, string.Empty);
    }

    /// <summary>Shows the outcome of a check.</summary>
    public void ApplyResult(RowCheckResult result)
    {
        IsFlaggedDuplicate = result.State == RowCheckState.InvalidFormat && result.Message == RowValidator.DuplicateMessage;
        switch (result.State)
        {
            case RowCheckState.Valid:
                SetStatus(RowStatusKind.Valid, "✓ " + (result.DisplayText ?? result.Message));
                break;
            case RowCheckState.NotFound:
                SetStatus(RowStatusKind.NotFound, "Not found on RSI");
                break;
            case RowCheckState.InvalidFormat:
                SetStatus(RowStatusKind.Invalid, result.Message);
                break;
            default:
                SetStatus(RowStatusKind.Failed, result.Message);
                break;
        }
    }

    private void SetStatus(RowStatusKind kind, string text)
    {
        _statusKind = kind;
        _statusText = text;
        Raise(nameof(StatusKind));
        Raise(nameof(StatusText));
        Raise(nameof(IsChecking));
    }
}

/// <summary>A row of the Players tab.</summary>
public sealed class PlayerRowViewModel : ListRowViewModel
{
    public const string ExclusionNote = "On the exclusion list: still matched because it is on your player list";

    private readonly Func<string, bool> _isExcluded;

    public PlayerRowViewModel(string handle, OverlayColor color, Func<string, bool> isExcluded)
        : base(handle, color)
    {
        _isExcluded = isExcluded;
        UpdateNote();
    }

    protected override void OnValueEdited() => UpdateNote();

    /// <summary>The exclusion list changed: show or hide the "on the exclusion list" note for this row.</summary>
    public void RefreshExclusionNote() => UpdateNote();

    private void UpdateNote()
    {
        var text = Value.Trim();
        Note = RsiNames.IsValidHandle(text) && _isExcluded(text) ? ExclusionNote : string.Empty;
    }
}

/// <summary>A row of the Orgs tab.</summary>
public sealed class OrgRowViewModel : ListRowViewModel
{
    private int _priority;

    public OrgRowViewModel(string sid, int priority, OverlayColor color)
        : base(sid, color)
    {
        _priority = priority;
    }

    /// <summary>1 = highest.</summary>
    public int Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }
}
