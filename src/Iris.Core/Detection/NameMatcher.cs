namespace Iris.Core.Detection;

/// <summary>
/// Fuzzy comparison of a read name with the names that are known to be on screen, as a developer tool for judging the reader (the
/// replay tool uses it to score a read against a label list; the app itself looks names up exactly). The cost of an edit follows
/// what the nameplate font confuses: swapping two characters of one confusable group costs 0.35, everything else costs 1. A port
/// of <c>weighted_distance</c> and <c>best_match</c> from the Python training kit.
/// </summary>
public static class NameMatcher
{
    /// <summary>Characters this font commonly confuses (0 O Q D, 1 I L 7, 5 S, 8 B, 6 G, 2 Z, K N X).</summary>
    private static readonly string[] ConfusableGroups = { "0OQD", "1IL7", "5S", "8B", "6G", "2Z", "KNX" };

    /// <summary>Cost of reading <paramref name="a"/> where <paramref name="b"/> is: 0 equal, 0.35 confusable, 1 otherwise.</summary>
    public static double SubstitutionCost(char a, char b)
    {
        if (a == b)
            return 0.0;
        foreach (var group in ConfusableGroups)
            if (group.Contains(a) && group.Contains(b))
                return 0.35;
        return 1.0;
    }

    /// <summary>Edit distance between two names with the substitution costs above; insertions and deletions cost 1.</summary>
    public static double WeightedDistance(string s, string t)
    {
        var previous = new double[t.Length + 1];
        var current = new double[t.Length + 1];
        for (var j = 0; j <= t.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= s.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var best = previous[j] + 1;
                var insert = current[j - 1] + 1;
                if (insert < best)
                    best = insert;
                var substitute = previous[j - 1] + SubstitutionCost(s[i - 1], t[j - 1]);
                if (substitute < best)
                    best = substitute;
                current[j] = best;
            }

            (previous, current) = (current, previous);
        }

        return previous[t.Length];
    }

    /// <summary>
    /// The name of <paramref name="vocabulary"/> that the imperfect <paramref name="text"/> most likely is, or null. Candidates
    /// whose length differs by more than 2 are skipped; the best one counts when its distance is at most max(1, 20% of its length).
    /// Ties go to the first of the vocabulary.
    /// </summary>
    public static string? BestMatch(string text, IEnumerable<string> vocabulary)
    {
        string? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in vocabulary)
        {
            if (Math.Abs(candidate.Length - text.Length) > 2)
                continue;
            var distance = WeightedDistance(text, candidate);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        if (best is null)
            return null;
        var limit = Math.Max(1.0, 0.2 * best.Length);
        return bestDistance <= limit ? best : null;
    }
}
