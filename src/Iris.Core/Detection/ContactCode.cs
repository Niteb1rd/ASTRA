namespace Iris.Core.Detection;

/// <summary>
/// Contact codes: the labels the game puts on unidentified contacts and some ships, such as <c>PA-5968-XF</c>: two letters, four
/// digits, two letters; each of the two hyphens is present or not, independently (8, 9 or 10 characters, as in the Python reference).
/// They are not player handles, so a nameplate that reads like one is ignored unless the code is on the user's own player list (see <see cref="NameFilter"/>).
/// The accuracy look-alikes of <see cref="LookAlikes"/> are tolerated where the pattern wants letters or digits: a letter slot also takes
/// <c>0</c> and <c>1</c> (an <c>O</c> or <c>I</c> read as a digit), a digit slot also takes <c>O</c> and <c>I</c>.
/// </summary>
public static class ContactCode
{
    /// <summary>True when <paramref name="name"/> has the shape of a contact code (case-insensitive).</summary>
    public static bool IsContactCode(string? name)
    {
        if (name is null)
            return false;
        var length = name.Length;
        if (length < 8 || length > 10)
            return false;

        var at = 0;
        return Letters(name, ref at) && Hyphen(name, ref at)
            && Digits(name, ref at) && Hyphen(name, ref at)
            && Letters(name, ref at) && at == length;
    }

    private static bool Letters(string s, ref int at)
    {
        for (var i = 0; i < 2; i++, at++)
        {
            if (at >= s.Length)
                return false;
            var c = char.ToUpperInvariant(s[at]);
            if (!(c is >= 'A' and <= 'Z' or '0' or '1'))
                return false;
        }

        return true;
    }

    private static bool Digits(string s, ref int at)
    {
        for (var i = 0; i < 4; i++, at++)
        {
            if (at >= s.Length)
                return false;
            var c = char.ToUpperInvariant(s[at]);
            if (!(c is >= '0' and <= '9' or 'O' or 'I'))
                return false;
        }

        return true;
    }

    /// <summary>Takes a hyphen if there is one; it is optional.</summary>
    private static bool Hyphen(string s, ref int at)
    {
        if (at < s.Length && s[at] == '-')
            at++;
        return true;
    }
}
