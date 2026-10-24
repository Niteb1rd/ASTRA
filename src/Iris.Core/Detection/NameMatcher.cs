namespace Iris.Core.Detection;

/// <summary>
/// Comparison of a read name with the names that are known to be on screen, as a developer tool for judging the reader (the replay
/// tool uses it to score a read against a label list; the app itself looks names up exactly). A read matches a name only when the two
/// are the same name: equal except for upper/lower case. There is NO look-alike tolerance (the accuracy list of <see cref="LookAlikes"/>
/// is empty) and no edit tolerance: a name with one wrong, missing or extra character does not match.
/// </summary>
public static class NameMatcher
{
    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are the same name (see the class remarks).</summary>
    public static bool Matches(string a, string b) => LookAlikes.NamesEqual(a, b);

    /// <summary>
    /// The first name of <paramref name="vocabulary"/> that <paramref name="text"/> matches (see <see cref="Matches"/>), or null.
    /// A name that matches in the same case is preferred over one that differs in case.
    /// </summary>
    public static string? BestMatch(string text, IEnumerable<string> vocabulary)
    {
        string? otherCase = null;
        foreach (var candidate in vocabulary)
        {
            if (string.Equals(candidate, text, StringComparison.Ordinal))
                return candidate;
            if (otherCase is null && Matches(text, candidate))
                otherCase = candidate;
        }

        return otherCase;
    }
}
