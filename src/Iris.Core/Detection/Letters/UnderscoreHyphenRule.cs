namespace Iris.Core.Detection.Letters;

/// <summary>
/// The measurements of one bar glyph (an underscore or a hyphen), all relative to the capital height of its line (<see cref="UnderscoreHyphenRule.Measure"/>).
/// </summary>
/// <param name="Frac">The centre of the bar's ink rows as a fraction of the capital band: 0 is the top edge of the band, 1 its bottom edge (an underscore sits at about 1 and below, a hyphen in the middle).</param>
/// <param name="Thick">The thickness of the bar (its rows) in capital heights.</param>
/// <param name="Length">The length of the bar (its ink columns) in capital heights. Measured, but the verdict does not use it.</param>
public readonly record struct BarMeasurement(double Frac, double Thick, double Length);

/// <summary>
/// The underscore / hyphen rule: the letter model confuses a low bar with a middle one now and then (an underscore read as a hyphen: 16 of 3396 underscores of the test character),
/// and a hyphen at either end of a name voids the vote. A bar's height in the capital band tells them apart exactly, so every <c>_</c> or <c>-</c> the model read in a NAME (never in a
/// distance line) is measured on the reader's own mask. A port of <c>bar_features</c>, <c>us_verdict</c> and <c>Reader.us_correct</c> of <c>detector_ref.py</c> (training kit, README section
/// "Underscore versus hyphen rule"), which the kit calls <c>us_rule</c>.
/// <list type="number">
/// <item>The glyph is the mask columns [a, b) the reader cut it from (<see cref="PieceRead.CharacterSpans"/>, the same spans the O/0 pass uses), the band is
/// <see cref="NameplateFeatures.FindCapBand"/> of the row's own mask (top and bottom rows, inclusive).</item>
/// <item>Row sums <c>rs[y]</c> over the glyph's columns; the bar's rows are those with <c>rs &gt;= <see cref="RowFraction"/> x max(rs)</c> (this drops specks above and below it);
/// <c>r0</c> is the first and <c>r1</c> the last of them.</item>
/// <item><c>cap = max(1, bottom - top + 1)</c>; <c>frac = ((r0 + r1 + 1) / 2 - top) / cap</c> (the centre of the pixel rows r0..r1 is (r0 + r1 + 1) / 2); <c>thick = (r1 - r0 + 1) / cap</c>.</item>
/// <item>thick above <see cref="MaxThick"/>: no verdict (a letter fragment or a merged glyph, not a clean bar). Otherwise frac at least <see cref="UnderscoreMin"/>: <c>_</c>;
/// frac at most <see cref="HyphenMax"/>: <c>-</c>; in between, the undecided band, the model's character stays.</item>
/// </list>
/// Only the character at the glyph's index changes: the score and the pieces are untouched. It applies per ROW of a multi-row name (each row with its own band), and only when the
/// reader's spans match the text one to one (a failed cut leaves the line as read).
/// </summary>
public static class UnderscoreHyphenRule
{
    // The fitted rule: model/us-rule.json of the training kit (4150 bar glyphs: hyphens at frac 0.585 +- 0.015, underscores at 1.047 +- 0.020, a gap of 0.34 between the extremes of the two classes),
    // copied here on 2026-10-24. The rule decides by frac alone; thick only withholds the verdict.

    /// <summary>A bar whose centre is at least this far down the capital band is an underscore (<c>us_min</c>).</summary>
    public const double UnderscoreMin = 0.87;

    /// <summary>A bar whose centre is at most this far down the capital band is a hyphen (<c>hy_max</c>).</summary>
    public const double HyphenMax = 0.71;

    /// <summary>A bar thicker than this many capital heights gets no verdict (<c>max_thick</c>).</summary>
    public const double MaxThick = 0.45;

    /// <summary>A mask row belongs to the bar when its ink is at least this fraction of the busiest row of the glyph's columns (<c>row_frac</c>, <c>US_ROW_FRAC</c>).</summary>
    public const double RowFraction = 0.25;

    /// <summary>
    /// The geometry of the bar in mask columns [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="m"/>, relative to <paramref name="band"/> (<c>bar_features</c>).
    /// Null when the columns hold no ink.
    /// </summary>
    public static BarMeasurement? Measure(LineMask m, int start, int end, CapBand band)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, m.Width);
        if (end <= start)
            return null;

        var rowSums = new int[m.Height];
        var max = 0;
        for (var y = 0; y < m.Height; y++)
        {
            var sum = 0;
            for (var x = start; x < end; x++)
                sum += m.Data[y * m.Width + x];
            rowSums[y] = sum;
            if (sum > max)
                max = sum;
        }

        if (max <= 0)
            return null;

        var limit = RowFraction * max; // a double compare, as numpy's  rs >= row_frac * rs.max()
        int r0 = -1, r1 = -1;
        for (var y = 0; y < m.Height; y++)
        {
            if (rowSums[y] < limit)
                continue;
            if (r0 < 0)
                r0 = y;
            r1 = y;
        }

        // the columns with ink inside the bar's rows
        int c0 = -1, c1 = -1;
        for (var x = start; x < end; x++)
        {
            var ink = false;
            for (var y = r0; y <= r1 && !ink; y++)
                ink = m.Data[y * m.Width + x] != 0;
            if (!ink)
                continue;
            if (c0 < 0)
                c0 = x;
            c1 = x;
        }

        var cap = (double)Math.Max(1, band.Bottom - band.Top + 1);
        var frac = ((r0 + r1 + 1) / 2.0 - band.Top) / cap;
        var thick = (r1 - r0 + 1) / cap;
        var length = c0 < 0 ? 0.0 : (c1 - c0 + 1) / cap; // c0 is never negative here (the busiest row has ink); kept for safety
        return new BarMeasurement(frac, thick, length);
    }

    /// <summary>'_' or '-' when the measurement is clean enough and clearly low or middle, otherwise null (keep the model's character) (<c>us_verdict</c>).</summary>
    public static char? Verdict(BarMeasurement bar)
    {
        if (bar.Thick > MaxThick)
            return null;
        if (bar.Frac >= UnderscoreMin)
            return '_';
        if (bar.Frac <= HyphenMax)
            return '-';
        return null;
    }

    /// <summary>
    /// The text of ONE ROW of a NAME with every <c>_</c> and <c>-</c> replaced by the verdict of the rule (<c>Reader.us_correct</c>); no verdict keeps the model's character.
    /// <paramref name="read"/> must have its mask and character spans; when the spans do not match the text one to one the text is returned as read. Call it after the O/0 pass
    /// (that pass never changes a bar). Distance lines never come here.
    /// </summary>
    public static string Correct(PieceRead read, string text)
    {
        if (read.Mask is null || read.CharacterSpans is null || read.CharacterSpans.Count != text.Length)
            return text;
        if (text.IndexOf('_') < 0 && text.IndexOf('-') < 0)
            return text;
        var band = NameplateFeatures.FindCapBand(read.Mask);
        var chars = text.ToCharArray();
        for (var k = 0; k < chars.Length; k++)
        {
            if (chars[k] != '_' && chars[k] != '-')
                continue;
            var (start, end) = read.CharacterSpans[k];
            if (Measure(read.Mask, start, end, band) is not { } bar)
                continue;
            if (Verdict(bar) is { } verdict)
                chars[k] = verdict;
        }

        return new string(chars);
    }
}
