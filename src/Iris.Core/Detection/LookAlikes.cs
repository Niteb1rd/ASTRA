namespace Iris.Core.Detection;

/// <summary>
/// The two look-alike lists of IRIS, kept in one place. The nameplate font draws some characters almost alike; what a read may
/// confuse depends on what is at stake:
/// <list type="bullet">
/// <item><b>Accuracy list</b> (names): ONLY <c>0 &lt;-&gt; O</c> and <c>1 &lt;-&gt; I</c>. It is used everywhere two names are compared:
/// the player list, the exclusion list, contact-code detection and the replay tool's label matching; <see cref="NameVariants"/>
/// is ready for the look-alike retry of a hotkey lookup (step 5; not used by the app yet). Everything else must match exactly: a wrong name is worse than a missed one, because a
/// wrong name is looked up on the RSI site and shown on screen.</item>
/// <item><b>Efficiency list</b> (distance digits only): <c>0 O Q D</c>, <c>1 I 7</c>, <c>5 S</c>, <c>8 B</c>, <c>6 G</c>, <c>2 Z</c>.
/// A distance line is read with these look-alikes tolerated because its digits are checked against a strict shape ("835.5m") and it
/// is never used as a name. K, N, X and L are NOT in the list.</item>
/// </list>
/// Comparison of names is case-insensitive (handles are case-insensitive on the RSI site).
/// </summary>
public static class LookAlikes
{
    /// <summary>The accuracy list as pairs: <c>0O</c> and <c>1I</c>.</summary>
    public static IReadOnlyList<string> AccuracyGroups { get; } = new[] { "0O", "1I" };

    /// <summary>The efficiency list as groups; the first character of each group is the digit the others stand for.</summary>
    public static IReadOnlyList<string> EfficiencyGroups { get; } = new[] { "0OQD", "1I7", "5S", "8B", "6G", "2Z" };

    /// <summary>
    /// The letters of the efficiency list that are not digits themselves, upper case: <c>O Q D I S B G Z</c>. A distance line is read
    /// with the digits, these letters, <c>.</c>, <c>k</c> and <c>m</c> as the characters it may choose from.
    /// </summary>
    public const string EfficiencyLetters = "OQDISBGZ";

    /// <summary>
    /// One character in its accuracy-list form: upper case, and <c>O</c> becomes <c>0</c>, <c>I</c> becomes <c>1</c>.
    /// Two characters are accuracy look-alikes of each other exactly when their folded forms are equal.
    /// </summary>
    public static char FoldNameChar(char c)
    {
        c = char.ToUpperInvariant(c);
        return c switch
        {
            'O' => '0',
            'I' => '1',
            _ => c,
        };
    }

    /// <summary>A name in its accuracy-list form (see <see cref="FoldNameChar"/>); the empty string for null.</summary>
    public static string FoldName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = FoldNameChar(source[i]);
        });
    }

    /// <summary>True when the two names are the same except for upper/lower case and accuracy look-alikes (0/O, 1/I).</summary>
    public static bool NamesEqual(string? a, string? b) =>
        a is not null && b is not null && string.Equals(FoldName(a), FoldName(b), StringComparison.Ordinal);

    /// <summary>
    /// The names a lookup may try instead of <paramref name="name"/>, when the first lookup found nothing (the hotkey look-alike retry):
    /// every combination of swapping its <c>0</c>/<c>O</c> and <c>1</c>/<c>I</c> characters, fewest swaps first, the name itself
    /// not included. At most <paramref name="limit"/> variants are produced (names with more than 5 such characters are cut off).
    /// The case of the other characters is kept; a swapped <c>O</c> or <c>I</c> takes the case of the original letter.
    /// </summary>
    public static IReadOnlyList<string> NameVariants(string name, int limit = 31)
    {
        var positions = new List<int>();
        for (var i = 0; i < name.Length; i++)
            if (name[i] is '0' or 'O' or 'o' or '1' or 'I' or 'i')
                positions.Add(i);
        if (positions.Count > 5)
            positions.RemoveRange(5, positions.Count - 5);

        var masks = new List<int>();
        for (var mask = 1; mask < (1 << positions.Count); mask++)
            masks.Add(mask);
        masks.Sort((a, b) =>
        {
            var byCount = System.Numerics.BitOperations.PopCount((uint)a).CompareTo(System.Numerics.BitOperations.PopCount((uint)b));
            return byCount != 0 ? byCount : a.CompareTo(b);
        });

        var result = new List<string>();
        foreach (var mask in masks)
        {
            if (result.Count >= limit)
                break;
            var chars = name.ToCharArray();
            for (var bit = 0; bit < positions.Count; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                    continue;
                var at = positions[bit];
                chars[at] = Swap(chars[at]);
            }

            result.Add(new string(chars));
        }

        return result;
    }

    private static char Swap(char c) => c switch
    {
        '0' => 'O',
        'O' => '0',
        'o' => '0',
        '1' => 'I',
        'I' => '1',
        'i' => '1',
        _ => c,
    };

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
