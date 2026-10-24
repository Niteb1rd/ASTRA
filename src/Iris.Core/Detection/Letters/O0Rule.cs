using OpenCvSharp;

namespace Iris.Core.Detection.Letters;

/// <summary>
/// The outer size and the hole of one ring-shaped glyph (O or 0), in ORIGINAL screen pixels (multiples of 1/8: the glyph was measured at 8 times the size).
/// </summary>
public readonly record struct O0Measurement(double OuterWidth, double OuterHeight, double HoleWidth, double HoleHeight);

/// <summary>
/// The sub-pixel O/0 rule: the letter model cannot tell the letter O from the digit 0 at 11 to 22 pixels high (the 0 is only a little narrower), so every glyph the
/// model read as <c>O</c> or <c>0</c> in a NAME (never in a distance line) is measured on its own pixels. A port of <c>o0_measure</c>, <c>o0_prob</c>, <c>o0_verdict</c>,
/// <c>glyph_box</c> and <c>Reader.o0_correct</c> of <c>detector_ref.py</c> (training kit, README section "Sub-pixel O/0 rule").
/// <list type="number">
/// <item>The glyph's box (the reader's own cut: its mask columns and its ink rows, mapped back to screen pixels) plus <see cref="Pad"/> pixels of margin is cut from the ORIGINAL BGR frame,
/// the maximum of the colour channels is taken, enlarged 8 times (cubic), and thresholded halfway between its 10th and 99th percentile.</item>
/// <item>The glyph is the run of columns with the most ink (an edge of a neighbour in the margin is a separate run); its outer width and height are those of its ink; the hole is the largest
/// 4-connected background region of the glyph box that touches none of its four edges. No hole (not a ring), or a box wider than 1.6 times its height: no measurement, the model's character stays.</item>
/// <item>Two features, <c>a = outer_w / outer_h</c> and <c>b = hole_w / outer_h</c>, are projected on the fitted line (<see cref="WeightA"/>, <see cref="WeightB"/>); one Gaussian per class gives P(O).</item>
/// <item>P at least <see cref="Decide"/>: <c>O</c>; P at most 1 - <see cref="Decide"/>: <c>0</c>; otherwise the model's character stays.</item>
/// </list>
/// How the reader's cut is matched: the Python reference records, for every character of a read, the mask columns the reader cut it from (<see cref="PieceRead.CharacterSpans"/>:
/// the whole piece for a single letter, the dynamic programming cut for letters that touch); the C# reader records the same spans in the same order, and
/// <see cref="GlyphBox"/> maps them back with the mask's origin (<see cref="LineMask.OriginX"/>, <see cref="LineMask.ColumnOffset"/> and so on). When the numbers of spans and characters differ (a
/// cut failed) the line is left as read.
/// </summary>
public static class O0Rule
{
    // The fitted rule: model/o0-rule.json of the training kit (trained on the test clips name14 to name17: 496 O and 1491 zeros), copied here on 2026-10-24.
    // Sizes are enlarged 8 times (JSON "upscale").
    /// <summary>The weights of the two features along the Fisher line (<c>w</c> of the rule file).</summary>
    public const double WeightA = 0.5667556378738614;

    public const double WeightB = 0.8238859429181884;

    /// <summary>Mean and standard deviation of the O class and of the 0 class along the line.</summary>
    public const double MeanO = 0.7533676849758579;

    public const double SdO = 0.025796386202400184;

    public const double Mean0 = 0.6808192767983506;

    public const double Sd0 = 0.01720362317305907;

    /// <summary>P(O) at or above this gives O, at or below 1 minus this gives 0 (<c>decide</c>).</summary>
    public const double Decide = 0.95;

    /// <summary>Enlargement of the glyph cut-out (<c>upscale</c>).</summary>
    public const int Upscale = 8;

    /// <summary>Original pixels of margin around a glyph's box when it is cut out (<c>o0_pad</c>).</summary>
    public const int Pad = 3;

    /// <summary>
    /// Measures one glyph from its cut-out (BGR or gray, 8 bit, original pixels, a little margin all round). Null when the glyph is not a closed ring or the cut is not one glyph
    /// (<c>o0_measure</c>).
    /// </summary>
    public static O0Measurement? Measure(Mat crop)
    {
        if (crop.Empty() || crop.Depth() != MatType.CV_8U)
            throw new ArgumentException("Expected a non-empty 8 bit image.", nameof(crop));

        using var gray = new Mat();
        if (crop.Channels() == 1)
        {
            crop.CopyTo(gray);
        }
        else
        {
            var channels = Cv2.Split(crop);
            try
            {
                channels[0].CopyTo(gray);
                for (var i = 1; i < channels.Length; i++)
                    Cv2.Max(gray, channels[i], gray);
            }
            finally
            {
                foreach (var c in channels)
                    c.Dispose();
            }
        }

        using var asFloat = new Mat();
        gray.ConvertTo(asFloat, MatType.CV_32FC1);
        using var big = new Mat();
        Cv2.Resize(asFloat, big, new Size(0, 0), Upscale, Upscale, InterpolationFlags.Cubic);
        var width = big.Cols;
        var height = big.Rows;
        var values = new float[width * height];
        if (big.IsContinuous())
        {
            System.Runtime.InteropServices.Marshal.Copy(big.Data, values, 0, values.Length);
        }
        else
        {
            using var copy = big.Clone();
            System.Runtime.InteropServices.Marshal.Copy(copy.Data, values, 0, values.Length);
        }

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        var threshold = (float)((Percentile(sorted, 10) + Percentile(sorted, 99)) / 2.0);
        var mask = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
            mask[i] = values[i] > threshold ? (byte)1 : (byte)0;
        return MeasureMask(mask, width, height);
    }

    /// <summary>
    /// The measurement on an already thresholded picture of the glyph (<paramref name="mask"/>: 1 = ink, row-major, at 8 times the screen resolution; sizes are
    /// divided by <see cref="Upscale"/>): the second half of <c>o0_measure</c>.
    /// </summary>
    public static O0Measurement? MeasureMask(byte[] mask, int width, int height)
    {
        if (mask.Length != width * height)
            throw new ArgumentException("Mask data does not match its size.", nameof(mask));

        // the run of columns with ink that holds the most ink is the glyph
        var columns = new int[width];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                columns[x] += mask[y * width + x];
        int bestStart = -1, bestEnd = -1;
        long bestInk = -1;
        var at = 0;
        while (at < width)
        {
            if (columns[at] > 0)
            {
                var start = at;
                long ink = 0;
                while (at < width && columns[at] > 0)
                    ink += columns[at++];
                if (ink > bestInk)
                {
                    bestInk = ink;
                    bestStart = start;
                    bestEnd = at;
                }
            }

            at++;
        }

        if (bestStart < 0)
            return null;

        // the glyph: the rows with ink inside that run
        var glyphWidth = bestEnd - bestStart;
        int firstRow = -1, lastRow = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = bestStart; x < bestEnd; x++)
            {
                if (mask[y * width + x] == 0)
                    continue;
                if (firstRow < 0)
                    firstRow = y;
                lastRow = y;
                break;
            }
        }

        var glyphHeight = lastRow - firstRow + 1;
        if (glyphWidth > 1.6 * glyphHeight)
            return null;

        // 4-connected background regions of the glyph box; the hole is the largest one that touches none of its edges
        var label = new int[glyphWidth * glyphHeight];
        var queue = new int[glyphWidth * glyphHeight];
        var bestArea = 0;
        var holeWidth = 0;
        var holeHeight = 0;
        var next = 0;
        for (var start = 0; start < label.Length; start++)
        {
            if (label[start] != 0 || mask[(firstRow + start / glyphWidth) * width + bestStart + start % glyphWidth] != 0)
                continue;
            next++;
            int head = 0, tail = 0, area = 0;
            int minX = glyphWidth, minY = glyphHeight, maxX = -1, maxY = -1;
            queue[tail++] = start;
            label[start] = next;
            while (head < tail)
            {
                var cell = queue[head++];
                var cx = cell % glyphWidth;
                var cy = cell / glyphWidth;
                area++;
                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
                for (var d = 0; d < 4; d++)
                {
                    var nx = cx + (d == 0 ? -1 : d == 1 ? 1 : 0);
                    var ny = cy + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || ny < 0 || nx >= glyphWidth || ny >= glyphHeight)
                        continue;
                    var neighbour = ny * glyphWidth + nx;
                    if (label[neighbour] != 0 || mask[(firstRow + ny) * width + bestStart + nx] != 0)
                        continue;
                    label[neighbour] = next;
                    queue[tail++] = neighbour;
                }
            }

            var interior = minX > 0 && minY > 0 && maxX + 1 < glyphWidth && maxY + 1 < glyphHeight;
            if (interior && area > bestArea)
            {
                bestArea = area;
                holeWidth = maxX - minX + 1;
                holeHeight = maxY - minY + 1;
            }
        }

        if (bestArea == 0)
            return null;
        return new O0Measurement(glyphWidth / (double)Upscale, glyphHeight / (double)Upscale, holeWidth / (double)Upscale, holeHeight / (double)Upscale);
    }

    /// <summary>
    /// P(O) for a measurement (<c>o0_prob</c>): the features <c>outer_w / outer_h</c> and <c>hole_w / outer_h</c> on the fitted line, one Gaussian per class.
    /// The position on the line is clamped between the two class means first: the O Gaussian is wider than the 0 one, so without the clamp a glyph narrower than
    /// any zero would come out as an O (the wider curve wins far out in the tails). Narrower than the average 0 is a 0, wider than the average O is an O.
    /// </summary>
    public static double ProbabilityO(O0Measurement m)
    {
        var s = (m.OuterWidth / m.OuterHeight) * WeightA + (m.HoleWidth / m.OuterHeight) * WeightB;
        s = Math.Clamp(s, Math.Min(Mean0, MeanO), Math.Max(Mean0, MeanO));
        var lO = -0.5 * Math.Pow((s - MeanO) / SdO, 2) - Math.Log(SdO);
        var l0 = -0.5 * Math.Pow((s - Mean0) / Sd0, 2) - Math.Log(Sd0);
        var d = l0 - lO;
        if (d > 700)
            return 0.0;
        return 1.0 / (1.0 + Math.Exp(d));
    }

    /// <summary>'O' when P(O) is at least <see cref="Decide"/>, '0' when it is at most 1 - <see cref="Decide"/>, otherwise null (keep the model's character) (<c>o0_verdict</c>).</summary>
    public static char? Verdict(double probabilityO) =>
        probabilityO >= Decide ? 'O' : probabilityO <= 1.0 - Decide ? '0' : null;

    /// <summary>
    /// The box of the glyph cut from mask columns [<paramref name="start"/>, <paramref name="end"/>) in original frame pixels, (x0, y0, x1, y1) with x1 and y1 exclusive, plus
    /// <paramref name="pad"/> pixels of margin (<c>glyph_box</c>); the rows are the glyph's own ink rows. Null when the columns hold no ink.
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1)? GlyphBox(LineMask mask, int start, int end, int pad = Pad)
    {
        int firstRow = -1, lastRow = -1;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = start; x < end && x < mask.Width; x++)
            {
                if (mask.Data[y * mask.Width + x] == 0)
                    continue;
                if (firstRow < 0)
                    firstRow = y;
                lastRow = y;
                break;
            }
        }

        if (firstRow < 0)
            return null;
        var scale = NameplateFeatures.Scale;
        var x0 = mask.OriginX + (int)Math.Floor((mask.ColumnOffset + start) / (double)scale) - pad;
        var x1 = mask.OriginX + (int)Math.Ceiling((mask.ColumnOffset + end) / (double)scale) + pad;
        var y0 = mask.OriginY + (int)Math.Floor((mask.RowOffset + firstRow) / (double)scale) - pad;
        var y1 = mask.OriginY + (int)Math.Ceiling((mask.RowOffset + lastRow + 1) / (double)scale) + pad;
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// The text of a NAME read with every O and 0 replaced by the verdict of the rule (<c>Reader.o0_correct</c>); no verdict (not a ring, undecided P, the cut is not one glyph) keeps the model's
    /// character. <paramref name="read"/> must have its mask and character spans (see <see cref="NameplateReader.ReadPieces(LineMask, bool[], double)"/>);
    /// when the spans do not match the text one to one the text is returned as read. Distance lines never come here.
    /// </summary>
    public static string Correct(Mat bgr, PieceRead read, string text)
    {
        if (read.Mask is null || read.CharacterSpans is null || read.CharacterSpans.Count != text.Length)
            return text;
        if (text.IndexOf('O') < 0 && text.IndexOf('0') < 0)
            return text;
        var chars = text.ToCharArray();
        for (var k = 0; k < chars.Length; k++)
        {
            if (chars[k] != 'O' && chars[k] != '0')
                continue;
            var (start, end) = read.CharacterSpans[k];
            var box = GlyphBox(read.Mask, start, end);
            if (box is null)
                continue;
            var x0 = Math.Max(0, box.Value.X0);
            var y0 = Math.Max(0, box.Value.Y0);
            var x1 = Math.Min(bgr.Width, box.Value.X1);
            var y1 = Math.Min(bgr.Height, box.Value.Y1);
            if (x1 <= x0 || y1 <= y0)
                continue;
            using var crop = new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
            var measurement = Measure(crop);
            if (measurement is null)
                continue;
            if (Verdict(ProbabilityO(measurement.Value)) is { } verdict)
                chars[k] = verdict;
        }

        return new string(chars);
    }

    /// <summary>numpy.percentile with the default linear interpolation, on sorted values.</summary>
    private static double Percentile(float[] sorted, double q)
    {
        var position = (sorted.Length - 1) * q / 100.0;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
