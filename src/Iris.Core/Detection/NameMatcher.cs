namespace Iris.Core.Detection;

/// <summary>
/// Comparison of a read name with the names that are known to be on screen, as a developer tool for judging the reader (the replay
/// tool uses it to score a read against a label list; the app itself looks names up exactly, with the retry of
/// <see cref="LookAlikes.NameVariants"/>). A read matches a name when the two are equal except for upper/lower case and the accuracy
/// look-alikes <c>0/O</c> and <c>1/I</c> (<see cref="LookAlikes"/>). There is NO other edit tolerance: a name with one wrong, missing
/// or extra letter does not match.
/// </summary>
public static class NameMatcher
{
    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are the same name (see the class remarks).</summary>
    public static bool Matches(string a, string b) => LookAlikes.NamesEqual(a, b);

    /// <summary>
    /// The first name of <paramref name="vocabulary"/> that <paramref name="text"/> matches (see <see cref="Matches"/>), or null.
    /// Names that match exactly (same characters, same case) are preferred over names that need a look-alike swap.
    /// </summary>
    public static string? BestMatch(string text, IEnumerable<string> vocabulary)
    {
        string? swapped = null;
        foreach (var candidate in vocabulary)
        {
            if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
                return candidate;
            if (swapped is null && Matches(text, candidate))
                swapped = candidate;
        }

        return swapped;
    }
}
