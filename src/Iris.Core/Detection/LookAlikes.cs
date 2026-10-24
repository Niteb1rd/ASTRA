namespace Iris.Core.Detection;

/// <summary>
/// The look-alike lists of IRIS, kept in one place. The nameplate font draws some characters almost alike; what a read may confuse
/// depends on what is at stake:
/// <list type="bullet">
/// <item><b>Accuracy list</b> (names): EMPTY. Names match EXACTLY, apart from upper/lower case (handles are case-insensitive on the RSI
/// site): the player list, the exclusion list, the colour lookup and the replay tool's label matching all compare the characters as
/// they are, so <c>N1TEBIRD</c> is not <c>Nitebird</c> and <c>UNKN0WN</c> is not <c>UNKNOWN</c>. A wrong name is worse than a missed one:
/// it is looked up on the RSI site and shown on screen. There is no character-swap retry of a lookup either: a name that RSI does not
/// know is remembered by the negative cache (<c>not_found</c>, 30 days), so the same read is not asked again, while a different read is a
/// different name and may be asked. A misread is to be fixed with better training data, not with tolerance.</item>
/// <item><b>Efficiency list</b> (distance digits only): <c>0 O Q D</c>, <c>1 I 7</c>, <c>5 S</c>, <c>8 B</c>, <c>6 G</c>, <c>2 Z</c>.
/// A distance line is read with these look-alikes tolerated because its digits are checked against a strict shape ("835.5m") and it
/// is never used as a name. K, N, X and L are NOT in the list.</item>
/// </list>
/// </summary>
public static class LookAlikes
{
    /// <summary>The accuracy list (names): empty. Names match exactly, ignoring only upper/lower case.</summary>
    public static IReadOnlyList<string> AccuracyGroups { get; } = Array.Empty<string>();

    /// <summary>The efficiency list as groups; the first character of each group is the digit the others stand for.</summary>
    public static IReadOnlyList<string> EfficiencyGroups { get; } = new[] { "0OQD", "1I7", "5S", "8B", "6G", "2Z" };

    /// <summary>
    /// The letters of the efficiency list that are not digits themselves, upper case: <c>O Q D I S B G Z</c>. A distance line is read
    /// with the digits, these letters, <c>.</c>, <c>k</c> and <c>m</c> as the characters it may choose from.
    /// </summary>
    public const string EfficiencyLetters = "OQDISBGZ";

    /// <summary>True when the two names are the same except for upper/lower case. Nothing else is tolerated (the accuracy list is empty).</summary>
    public static bool NamesEqual(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name with every digit <c>0</c> turned into the letter <c>O</c>: ONLY the key of the O/0 evidence of <see cref="NameplateTrackerSettings.O0MultiFrame"/>
    /// (the frames of one plate that differ just in O/0 share their evidence). It is NOT used to compare names: the exclusion lists, the session exclusions and the
    /// player list all match exactly.
    /// </summary>
    public static string FoldO0(string name) => name.Replace('0', 'O');

    /// <summary>
    /// The digit that <paramref name="c"/> stands for in a distance (efficiency list): a digit stands for itself, <c>O Q D</c> for 0,
    /// <c>I</c> for 1, <c>S</c> for 5, <c>B</c> for 8, <c>G</c> for 6, <c>Z</c> for 2 (upper case letters only). Null for any other character.
    /// </summary>
    public static char? EfficiencyDigit(char c)
    {
        if (c is >= '0' and <= '9')
            return c;
        foreach (var group in EfficiencyGroups)
            if (group.IndexOf(c) > 0)
                return group[0];
        return null;
    }
}
