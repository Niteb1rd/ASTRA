using Iris.Core.Detection.Letters;

namespace Iris.Core.Detection;

/// <summary>A distance line that passed <see cref="DistanceLineRule"/>: "835.5m", "9.0km".</summary>
/// <param name="Text">The line as read, look-alike letters already turned into digits: <c>835.5m</c>.</param>
/// <param name="Value">The number in front of the unit: 835.5, 9.0.</param>
/// <param name="Kilometers">True for <c>km</c>, false for <c>m</c>.</param>
/// <param name="HasSpeedGroup">True when the line goes on after the unit with a speed group opened by <c>[</c> (<c>9.0km [-29m/s]</c>). The group itself is not read.</param>
public readonly record struct DistanceReading(string Text, double Value, bool Kilometers, bool HasSpeedGroup = false);

/// <summary>
/// The distance ANCHOR rule: decides from the model's restricted read of a line whether it is the distance line of a nameplate.
/// The line must read as exactly <c>number . digit unit</c>:
/// <list type="bullet">
/// <item>number: 1 to 4 digits, at most <see cref="NameplateDetectorSettings.MaxDistanceValue"/> (0 to 1000);</item>
/// <item>a period, then exactly one digit (distances without a decimal are ignored);</item>
/// <item>the unit, lower case <c>m</c> or <c>km</c>, and nothing after it;</item>
/// <item>no gap wider than a word gap (<see cref="NameplateDetectorSettings.WordGap"/> capital heights) between two pieces of the line, except ONE
/// gap right after the unit followed by a piece that reads as <c>[</c>: the speed readout of a targeted nameplate (<c>9.0km [-29m/s]</c>).
/// Everything from the bracket onward is ignored (not read, not scored, not validated); the line is judged on the pieces before the gap;</item>
/// <item>the read must be sure: mean log probability per character at least <see cref="NameplateDetectorSettings.MinDistanceScore"/>, and the
/// model's own opinion (best over all characters) may not be too far from the restricted read (mean gap at most
/// <see cref="NameplateDetectorSettings.MaxDistanceGap"/> over the first 6 pieces).</item>
/// </list>
/// The line is read with the characters <c>0-9 . k m</c> and the digit look-alikes <c>O Q D I S B G Z</c> (the efficiency list of
/// <see cref="LookAlikes"/>); a look-alike letter counts as the digit it stands for. Port of <c>Reader.read_distance</c> of the Python reference.
/// </summary>
public static class DistanceLineRule
{
    /// <summary>The characters the distance line is read with: digits, ".", "k", "m" and the efficiency look-alike letters.</summary>
    public static string Alphabet { get; } = "0123456789.km" + LookAlikes.EfficiencyLetters;

    /// <summary>Evaluates a read; null when the line is not a distance anchor.</summary>
    /// <param name="read">The line read with <see cref="Alphabet"/>.</param>
    /// <param name="settings">The detector settings.</param>
    /// <param name="isOpenBracket">Tells whether a piece of the line reads as <c>[</c>; null: no speed group is accepted.</param>
    public static DistanceReading? Evaluate(PieceRead? read, NameplateDetectorSettings settings, Func<ReadPiece, bool>? isOpenBracket = null)
    {
        if (read is null || read.Pieces.Count == 0)
            return null;
        var pieces = read.Pieces;
        var cap = read.CapHeight;

        // a first or last piece the model is unsure about is a speck (noise next to the line), not part of it
        var first = 0;
        var last = pieces.Count - 1;
        while (last > first && pieces[last].Score < settings.DistanceEndScore)
            last--;
        while (first < last && pieces[first].Score < settings.DistanceEndScore)
            first++;
        if (first != 0 || last != pieces.Count - 1)
            pieces = pieces.Skip(first).Take(last - first + 1).ToList();

        // a word gap inside the line is allowed once, when the piece after the FIRST gap is the "[" of a speed group; the rest is ignored
        var speedGroup = false;
        for (var i = 0; i + 1 < pieces.Count; i++)
        {
            if (pieces[i + 1].Start - pieces[i].End <= settings.WordGap * cap)
                continue;
            if (isOpenBracket is null || !isOpenBracket(pieces[i + 1]))
                return null; // a space inside the line: not one distance
            pieces = pieces.Take(i + 1).ToList();
            speedGroup = true;
            break;
        }

        var text = new System.Text.StringBuilder();
        var chars = 0;
        var total = 0.0;
        foreach (var piece in pieces)
        {
            foreach (var ch in piece.Text)
                text.Append(LookAlikes.EfficiencyDigit(ch) ?? ch);
            chars += piece.Text.Length;
            total += piece.Score;
        }

        var mean = total / Math.Max(1, chars);

        var gapSum = 0.0;
        var count = Math.Min(6, pieces.Count);
        for (var i = 0; i < count; i++)
            gapSum += (pieces[i].FreeScore - pieces[i].Score) / Math.Max(1, pieces[i].Text.Length);
        var gap = gapSum / count;

        if (mean < settings.MinDistanceScore || gap > settings.MaxDistanceGap)
            return null;

        var parsed = Parse(text.ToString(), settings.MaxDistanceValue);
        return parsed is { } reading ? reading with { HasSpeedGroup = speedGroup } : null;
    }

    /// <summary>
    /// The shape check alone: <c>[0-9]{1,4} . [0-9] (km|m)</c> with the number at most <paramref name="maxValue"/>. Null when the text
    /// does not have that shape.
    /// </summary>
    public static DistanceReading? Parse(string text, int maxValue)
    {
        var at = 0;
        while (at < text.Length && at < 4 && text[at] is >= '0' and <= '9')
            at++;
        if (at == 0 || at >= text.Length || text[at] != '.')
            return null;
        var whole = int.Parse(text.AsSpan(0, at), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        if (whole > maxValue)
            return null;
        var dot = at;
        if (dot + 1 >= text.Length || text[dot + 1] is not (>= '0' and <= '9'))
            return null;
        var tenth = text[dot + 1] - '0';
        var unit = text.AsSpan(dot + 2);
        bool kilometers;
        if (unit.SequenceEqual("km"))
            kilometers = true;
        else if (unit.SequenceEqual("m"))
            kilometers = false;
        else
            return null;
        return new DistanceReading(text, whole + tenth / 10.0, kilometers);
    }
}
