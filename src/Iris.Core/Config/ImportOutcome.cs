namespace Iris.Core.Config;

/// <summary>How an import attempt ended.</summary>
public enum ImportOutcome
{
    /// <summary>The list file was changed.</summary>
    Imported,

    /// <summary>The user said no (or closed a dialog); nothing was changed.</summary>
    Cancelled,

    /// <summary>The text has errors; nothing was changed. The user has been told.</summary>
    Rejected,

    /// <summary>The user chose "save my changes first", but that save failed or was refused; nothing was imported.</summary>
    NotSaved,

    /// <summary>Writing the list file failed; nothing was changed. The user has been told.</summary>
    WriteFailed,

    /// <summary>Another import was already running.</summary>
    Busy,
}
