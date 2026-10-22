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

/// <summary>What the hosted-config check does with each <see cref="ImportOutcome"/>.</summary>
public static class ImportOutcomes
{
    /// <summary>
    /// True when the hosted file's hash should be remembered (so the same version is not offered again): the user applied it,
    /// declined it, or it is broken. For the other outcomes the user has not really decided, so it is offered again next time.
    /// </summary>
    public static bool ShouldRememberHostedVersion(ImportOutcome outcome) =>
        outcome is ImportOutcome.Imported or ImportOutcome.Cancelled or ImportOutcome.Rejected;

    /// <summary>The sentence "Update database now" reports for the hosted config.</summary>
    /// <param name="outcome">How the import ended.</param>
    /// <param name="message">The import's own message (a summary, or the reason).</param>
    public static string HostedMessage(ImportOutcome outcome, string message)
    {
        switch (outcome)
        {
            case ImportOutcome.Imported:
                return "Hosted config imported. " + message;
            case ImportOutcome.Cancelled:
                return "Hosted config change skipped.";
            case ImportOutcome.Rejected:
                return "Hosted config was not imported (it has problems).";
            default:
                var reason = message.Trim();
                if (reason.Length > 0 && !reason.EndsWith('.') && !reason.EndsWith('!') && !reason.EndsWith('?'))
                    reason += ".";
                return "Hosted config was not imported: " + reason + " It will be offered again next time.";
        }
    }
}
