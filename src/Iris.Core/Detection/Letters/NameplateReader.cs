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
/// One run of connected ink in a line, read as one or more letters (more than one when letters touch and had to be cut apart).
/// </summary>
/// <param name="Start">First mask column of the piece.</param>
/// <param name="End">One past the last mask column.</param>
/// <param name="Text">The characters read (one, or several for a cut piece; empty when a cut failed).</param>
/// <param name="Score">Sum of the log probabilities of the characters, among the characters the read was allowed to choose from.</param>
/// <param name="FreeScore">The same sum if every character of the model had been allowed. Equal to <paramref name="Score"/> for a cut
/// piece and for a read without a restriction; the difference shows how much the restriction had to bend the model's opinion.</param>
public readonly record struct ReadPiece(int Start, int End, string Text, double Score, double FreeScore);

/// <summary>A line read piece by piece: the pieces and the capital height of the line in mask pixels.</summary>
public sealed record PieceRead(IReadOnlyList<ReadPiece> Pieces, int CapHeight);

/// <summary>
/// Reads the name on one nameplate line with the trained <see cref="LetterModel"/>: mask, cut at blank columns, classify each
/// piece; pieces that are too wide for one letter (letters touching) are cut by a dynamic-programming search for the cut set
/// whose letters the model is surest about. A port of <c>Recognizer</c> in the Python prototype (<c>read</c>, <c>read_mask</c>,
/// <c>_split</c>), kept step for step. The reader holds no mutable state, so one instance can be shared between threads.
/// </summary>
public sealed class NameplateReader
{
    private const double SplitFailureScore = -50.0;

    /// <summary>
    /// A run of touching ink wider than this many capital heights is not text (noise or a bright background) and is not cut: the
    /// search would cost thousands of letter reads. The widest real run in the training clips is 4.1 capital heights.
    /// </summary>
    public const double MaxPieceCaps = 8.0;

    /// <summary>A cut search over more candidate letter spans than this is not tried either (real runs need at most about 600).</summary>
    public const int MaxSplitSpans = 1500;

    /// <summary>
    /// A piece of ink with less ink than this fraction of (capital height)^2 is noise and is skipped. Names use this value.
    /// A distance line needs its decimal point, a tiny speck: it is read in distance mode with a much smaller limit
    /// (<see cref="NameplateDetectorSettings.DistanceMinInk"/> in the detector).
    /// </summary>
    public const double NameMinInk = 0.02;

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
    public NameplateRead ReadMask(LineMask m) => ReadCore(m, null, false, NameMinInk, null, out _);

    /// <summary>
    /// Makes the list of characters a restricted read may choose from: element i is true when output i of the model is one of
    /// <paramref name="characters"/>. Throws when none of them is in the model's charset.
    /// </summary>
    public bool[] CreateAlphabet(string characters)
    {
        var allowed = new bool[_model.ClassCount];
        var any = false;
        for (var i = 0; i < allowed.Length; i++)
        {
            if (characters.Contains(_model.Charset[i]))
            {
                allowed[i] = true;
                any = true;
            }
        }

        if (!any)
            throw new ArgumentException("None of the characters is in the model's charset.", nameof(characters));
        return allowed;
    }

    /// <summary>
    /// Reads the line inside <paramref name="box"/> piece by piece, choosing every letter among <paramref name="allowed"/> only
    /// (null: the whole charset), and giving up on runs of ink that are too wide to be text (<see cref="MaxPieceCaps"/>,
    /// <see cref="MaxSplitSpans"/>), which keeps the time per line bounded on noise. Returns null when the box holds no text.
    /// <paramref name="minInk"/> is the noise limit (see <see cref="NameMinInk"/>); the distance mode of the detector passes a smaller one
    /// so that the period of "5.8km" is kept.
    /// </summary>
    public PieceRead? ReadPieces(Mat bgr, Rect box, bool[]? allowed = null, double minInk = NameMinInk)
    {
        var mask = NameplateFeatures.CreateMask(bgr, box);
        return mask is null ? null : ReadPieces(mask, allowed, minInk);
    }

    /// <summary>The mask overload of <see cref="ReadPieces(Mat, Rect, bool[], double)"/>.</summary>
    public PieceRead ReadPieces(LineMask m, bool[]? allowed = null, double minInk = NameMinInk)
    {
        if (allowed is not null && allowed.Length != _model.ClassCount)
            throw new ArgumentException("The alphabet has to have one entry per character of the model.", nameof(allowed));
        var pieces = new List<ReadPiece>();
        ReadCore(m, allowed, true, minInk, pieces, out var cap);
        return new PieceRead(pieces, cap);
    }

    /// <summary>
    /// <c>read_mask</c> with two additions that the plain <see cref="ReadMask"/> leaves out: an optional restriction of the
    /// alphabet, (<paramref name="limitWork"/>) the work limits for runs of ink that are too wide to be text, and the noise limit <paramref name="minInk"/>.
    /// </summary>
    private NameplateRead ReadCore(LineMask m, bool[]? allowed, bool limitWork, double minInk, List<ReadPiece>? pieces, out int capHeight)
    {
        var band = NameplateFeatures.FindCapBand(m);
        var cap = band.Bottom - band.Top + 1;
        capHeight = cap;
        var text = new StringBuilder();
        var total = 0.0;

        var cuts = NameplateFeatures.GapCuts(m);
        if (cuts.Count == 0)
            return new NameplateRead("", 0.0);

        // Work space for the whole call, made once: a letter vector, the log probabilities and the network's buffers on the stack
        // (heap for unusually large models), and the Mats for the letter pictures. Expected call counts: one GlyphVector +
        // PredictLogProbs per separate piece, and per touching piece one per candidate span (pairs of cut candidates no wider than
        // 1.2 capital heights), so a line costs about as many calls as it has letters plus a few per touching pair. None allocates.
        Span<double> vector = stackalloc double[NameplateFeatures.VectorLength];
        Span<double> lp = _model.ClassCount <= 256 ? stackalloc double[_model.ClassCount] : new double[_model.ClassCount];
        Span<double> network = _model.ScratchSize <= 2048 ? stackalloc double[_model.ScratchSize] : new double[_model.ScratchSize];
        using var scratch = new GlyphScratch(m.Height, m.Width);

        foreach (var (a, b) in cuts)
        {
            var ink = 0;
            for (var y = 0; y < m.Height; y++)
                for (var x = a; x < b; x++)
                    ink += m.Data[y * m.Width + x];
            if (ink < minInk * cap * cap) // specks of noise
                continue;

            if (b - a <= 1.2 * cap)
            {
                if (!NameplateFeatures.GlyphVector(m, a, b, band, vector, scratch))
                    continue;
                _model.PredictLogProbs(vector, lp, network);
                var best = ArgMax(lp, allowed);
                text.Append(_model.Charset[best]);
                total += lp[best];
                if (pieces is not null)
                {
                    var free = allowed is null ? best : ArgMax(lp, null);
                    pieces.Add(new ReadPiece(a, b, _model.Charset[best].ToString(), lp[best], lp[free]));
                }
            }
            else
            {
                var (piece, score) = Split(m, a, b, band, allowed, limitWork, vector, lp, network, scratch);
                text.Append(piece);
                total += score;
                pieces?.Add(new ReadPiece(a, b, piece, score, score));
            }
        }

        return new NameplateRead(text.ToString(), total);
    }

    /// <summary>Best way to cut columns [x0, x1) into letters (<c>_split</c>).</summary>
    private (string Text, double Score) Split(LineMask m, int x0, int x1, CapBand band, bool[]? allowed, bool limitWork, Span<double> vector, Span<double> lp, Span<double> network, GlyphScratch scratch)
    {
        var cap = band.Bottom - band.Top + 1;
        var width = x1 - x0;
        if (limitWork && width > MaxPieceCaps * cap)
            return ("", SplitFailureScore);

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

        if (limitWork)
        {
            // count the candidate spans before reading any of them (same pairs as the loop below)
            var pairCount = 0;
            for (var i = 0; i < cand.Count; i++)
            {
                for (var j = i + 1; j < cand.Count; j++)
                {
                    var span = cand[j] - cand[i];
                    if (span > 1.2 * cap)
                        break;
                    if (span < 0.25 * cap)
                        continue;
                    pairCount++;
                }
            }

            if (pairCount > MaxSplitSpans)
                return ("", SplitFailureScore);
        }

        // every candidate span that could be one letter, with its best character and log probability
        var spanFrom = new List<int>();
        var spanTo = new List<int>();
        var spanChar = new List<int>();
        var spanLogP = new List<double>();
        for (var i = 0; i < cand.Count; i++)
        {
            for (var j = i + 1; j < cand.Count; j++)
            {
                var span = cand[j] - cand[i];
                if (span > 1.2 * cap)
                    break;
                if (span < 0.25 * cap)
                    continue;
                if (!NameplateFeatures.GlyphVector(m, x0 + cand[i], x0 + cand[j], band, vector, scratch))
                    continue;
                _model.PredictLogProbs(vector, lp, network);
                var best = ArgMax(lp, allowed);
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

    /// <summary>
    /// True when the model reads the columns [<paramref name="start"/>, <paramref name="end"/>) of the mask as the opening bracket <c>[</c>: its best
    /// character among ALL of its characters is <c>[</c>, with a log probability of at least <paramref name="minScore"/>. False when the columns hold
    /// no ink or the model has no <c>[</c>. The detector asks this of the piece after the word gap that follows the unit of a distance line
    /// (the speed readout <c>9.0km [-29m/s]</c>). Port of <c>Reader.is_open_bracket</c> of the Python reference.
    /// </summary>
    public bool IsOpenBracket(LineMask m, int start, int end, double minScore)
    {
        var bracket = _model.Charset.IndexOf('[');
        if (bracket < 0)
            return false;
        var band = NameplateFeatures.FindCapBand(m);
        var vector = NameplateFeatures.GlyphVector(m, start, end, band);
        if (vector is null)
            return false;
        var lp = _model.PredictLogProbs(vector);
        var best = ArgMax(lp, null);
        return best == bracket && lp[best] >= minScore;
    }

    /// <summary>
    /// Index of the first largest value (numpy.argmax), among the entries that <paramref name="allowed"/> lets through (null: all).
    /// Callers check that at least one entry is allowed.
    /// </summary>
    private static int ArgMax(ReadOnlySpan<double> values, bool[]? allowed)
    {
        var best = -1;
        for (var i = 0; i < values.Length; i++)
        {
            if (allowed is not null && !allowed[i])
                continue;
            if (best < 0 || values[i] > values[best])
                best = i;
        }

        return Math.Max(best, 0);
    }
}
