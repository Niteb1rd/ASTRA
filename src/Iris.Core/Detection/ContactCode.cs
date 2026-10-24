namespace Iris.Core.Detection;

/// <summary>
/// Contact codes: the labels the game puts on unidentified contacts and some ships, such as <c>PA-5968-XF</c>. The shape is exact:
/// 2 characters, a hyphen, 4 characters, a hyphen, 2 characters (10 in all), where every character is a letter or a digit. They are
/// not player handles, so a nameplate that reads like one is ignored unless the code is on the user's own player list (see
/// <see cref="NameFilter"/>). New RSI handles can no longer contain hyphens (probably so players can't pose as contacts), so the two
/// hyphens make the shape unambiguous and the check fast; letters and digits are not told apart, so no look-alike question arises.
/// </summary>
public static class ContactCode
{
    /// <summary>True when <paramref name="name"/> has the shape of a contact code (case-insensitive).</summary>
    public static bool IsContactCode(string? name)
    {
        if (name is null || name.Length != 10 || name[2] != '-' || name[7] != '-')
            return false;
        for (var i = 0; i < name.Length; i++)
        {
            if (i is 2 or 7)
                continue;
            if (!char.IsAsciiLetterOrDigit(name[i]))
                return false;
        }

        return true;
    }
}
