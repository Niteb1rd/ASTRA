using System.Text;
using OpenCvSharp;

namespace Iris.Core.Detection.Letters;

/// <summary>What the reader made of one line: the text and the sum of the log probabilities of its letters (closer to 0 is surer).</summary>
public readonly record struct NameplateRead(string Text, double Score)
{
    /// <summary>Nothing readable in the box.</summary>
    public static NameplateRead Empty { get; } = new("", 0.0);
}

/// <summary>
/// Reads the name on one nameplate line with the trained <see cref="LetterModel"/>: mask, cut at blank columns, classify each
/// piece; pieces that are too wide for one letter (letters touching) are cut by a dynamic-programming search for the cut set
/// whose letters the model is surest about. A port of <c>Recognizer</c> in the Python prototype (<c>read</c>, <c>read_mask</c>,
/// <c>_split</c>), kept step for step. The reader holds no mutable state, so one instance can be shared between threads.
/// </summary>
public sealed class NameplateReader
{
    private const double SplitFailureScore = -50.0;

    private readonly LetterModel _model;

    public NameplateReader(LetterModel model)
    {
        if (model.InputSize != NameplateFeatures.VectorLength)
            throw new ArgumentException($"The model expects {model.InputSize} inputs, the features have {NameplateFeatures.VectorLength}.", nameof(model));
        _model = model;
    }

    /// <summary>Reads the nameplate line inside <paramref name="box"/> (screen pixels) of a BGR 8 bit image.</summary>
    public NameplateRead Read(Mat bgr, Rect box)
    {
        var mask = NameplateFeatures.CreateMask(bgr, box);
        return mask is null ? NameplateRead.Empty : ReadMask(mask);
    }

    /// <summary>Reads from a pixel buffer: 3 bytes per pixel (B, G, R), rows <paramref name="stride"/> bytes apart.</summary>
    public NameplateRead Read(byte[] bgr, int width, int height, int stride, Rect box)
    {
        var mask = NameplateFeatures.CreateMask(bgr, width, height, stride, box);
        return mask is null ? NameplateRead.Empty : ReadMask(mask);
    }

    /// <summary>Reads an already prepared mask (<c>read_mask</c>).</summary>
    public NameplateRead ReadMask(LineMask m)
    {
        var band = NameplateFeatures.FindCapBand(m);
        var cap = band.Bottom - band.Top + 1;
        var text = new StringBuilder();
        var total = 0.0;
        Span<double> lp = _model.ClassCount <= 256 ? stackalloc double[_model.ClassCount] : new double[_model.ClassCount];

        foreach (var (a, b) in NameplateFeatures.GapCuts(m))
        {
            var ink = 0;
            for (var y = 0; y < m.Height; y++)
                for (var x = a; x < b; x++)
                    ink += m.Data[y * m.Width + x];
            if (ink < 0.02 * cap * cap) // specks of noise
                continue;

            if (b - a <= 1.2 * cap)
            {
                var vector = NameplateFeatures.GlyphVector(m, a, b, band);
                if (vector is null)
                    continue;
                _model.PredictLogProbs(vector, lp);
                var best = ArgMax(lp);
                text.Append(_model.Charset[best]);
                total += lp[best];
            }
            else
            {
                var (piece, score) = Split(m, a, b, band);
                text.Append(piece);
                total += score;
            }
        }

        return new NameplateRead(text.ToString(), total);
    }

    /// <summary>Best way to cut columns [x0, x1) into letters (<c>_split</c>).</summary>
    private (string Text, double Score) Split(LineMask m, int x0, int x1, CapBand band)
    {
        var cap = band.Bottom - band.Top + 1;
        var width = x1 - x0;

        // column ink profile of the piece
        var col = new int[width];
        for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < width; x++)
                col[x] += m.Data[y * m.Width + x0 + x];

        // candidate cut positions: both ends and the local minima of the profile that are less than half a letter tall
        var cand = new List<int> { 0 };
        for (var x = 2; x < width - 2; x++)
            if (col[x] <= col[x - 1] && col[x] <= col[x + 1] && col[x] <= 0.5 * cap)
                cand.Add(x);
        cand.Add(width);

        // every candidate span that could be one letter, with its best character and log probability
        var spanFrom = new List<int>();
        var spanTo = new List<int>();
        var spanChar = new List<int>();
        var spanLogP = new List<double>();
        var lp = new double[_model.ClassCount];
        for (var i = 0; i < cand.Count; i++)
        {
            for (var j = i + 1; j < cand.Count; j++)
            {
                var span = cand[j] - cand[i];
                if (span > 1.2 * cap)
                    break;
                if (span < 0.25 * cap)
                    continue;
                var vector = NameplateFeatures.GlyphVector(m, x0 + cand[i], x0 + cand[j], band);
                if (vector is null)
                    continue;
                _model.PredictLogProbs(vector, lp);
                var best = ArgMax(lp);
                spanFrom.Add(i);
                spanTo.Add(j);
                spanChar.Add(best);
                spanLogP.Add(lp[best]);
            }
        }

        if (spanFrom.Count == 0)
            return ("", SplitFailureScore);

        // dynamic programming over the cut positions (spans are already ordered by start, as in Python)
        var score = new double[cand.Count];
        var backFrom = new int[cand.Count];
        var backChar = new int[cand.Count];
        Array.Fill(score, -1e9);
        Array.Fill(backFrom, -1);
        score[0] = 0.0;
        for (var k = 0; k < spanFrom.Count; k++)
        {
            var from = spanFrom[k];
            var to = spanTo[k];
            var s = score[from] + spanLogP[k];
            if (score[from] > -1e8 && s > score[to])
            {
                score[to] = s;
                backFrom[to] = from;
                backChar[to] = spanChar[k];
            }
        }

        var end = cand.Count - 1;
        if (score[end] <= -1e8)
            return ("", SplitFailureScore);

        var letters = new List<char>();
        var at = end;
        while (at != 0 && backFrom[at] >= 0)
        {
            letters.Add(_model.Charset[backChar[at]]);
            at = backFrom[at];
        }

        letters.Reverse();
        return (new string(letters.ToArray()), score[end]);
    }

    /// <summary>Index of the first largest value (numpy.argmax).</summary>
    private static int ArgMax(ReadOnlySpan<double> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
            if (values[i] > values[best])
                best = i;
        return best;
    }
}
