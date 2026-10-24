namespace Iris.Core.Detection;

/// <summary>
/// Contact codes: the labels the game puts on unidentified contacts and some ships, such as <c>PA-5968-XF</c>: exactly two letters, a
/// hyphen, four digits, a hyphen, two letters (10 characters). They are not player handles, so a nameplate that reads like one is ignored
/// unless the code is on the user's own player list (see <see cref="NameFilter"/>). The check is exact, with no look-alike tolerance: the
/// two hyphens make the shape unambiguous, and a code read slightly wrong is simply not ignored (the overlay shows it with the No Match
/// rule) rather than risking that a real handle without hyphens is taken for a code.
/// </summary>
public static class ContactCode
{
    /// <summary>True when <paramref name="name"/> has the shape of a contact code (case-insensitive).</summary>
    public static bool IsContactCode(string? name)
    {
        if (name is null || name.Length != 10 || name[2] != '-' || name[7] != '-')
            return false;
        return IsLetter(name[0]) && IsLetter(name[1])
            && char.IsAsciiDigit(name[3]) && char.IsAsciiDigit(name[4]) && char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6])
            && IsLetter(name[8]) && IsLetter(name[9]);
    }

    private static bool IsLetter(char c) => char.ToUpperInvariant(c) is >= 'A' and <= 'Z';
}
