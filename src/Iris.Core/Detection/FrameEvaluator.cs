using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>
/// Evaluates the candidate pairs of ONE frame (create it with <see cref="NameplateDetector.CreateEvaluator"/>). A strict or loose pair is
/// evaluated with <see cref="NameplateDetector.Evaluate"/>. A <see cref="CrowdPair"/> (isolated except for the text of other pairs) is a nameplate only
/// when, besides passing that evaluation itself, EVERY text line beside it belongs to another pair that reads as a nameplate (a distance anchor
/// under one word of name characters). Clutter, HUD text and multi-word text beside a line never does, so such a pair is rejected. Results are
/// kept per frame (a pair read for one reason is not read again for another). A port of <c>FrameEvaluator</c> of <c>detector_ref.py</c>.
/// Not thread safe; the frame image must not change while the evaluator is used.
/// </summary>
public sealed class FrameEvaluator
{
    private readonly NameplateDetector _detector;
    private readonly Mat _frame;
    private readonly NameplateCandidates _candidates;
    private readonly Dictionary<(Rect Top, Rect Bottom), PairEvaluation> _memo = new();
    private readonly Dictionary<(Rect Top, Rect Bottom), IReadOnlyList<int>> _crowd = new();
    private readonly Dictionary<int, List<(Rect Top, Rect Bottom)>> _pairsOfLine = new();

    internal FrameEvaluator(NameplateDetector detector, Mat frame, NameplateCandidates candidates)
    {
        _detector = detector;
        _frame = frame;
        _candidates = candidates;
        foreach (var crowd in candidates.CrowdPairs)
            _crowd[(candidates.Boxes[crowd.Top], candidates.Boxes[crowd.Bottom])] = crowd.Neighbours;
        foreach (var (t, b) in AllPairs())
        {
            var pair = (candidates.Boxes[t], candidates.Boxes[b]);
            Add(t, pair);
            Add(b, pair);
        }
    }

    /// <summary>
    /// The pairs to follow with a <see cref="NameplateTracker"/>: strict pairs, then crowd pairs (strict: their evaluation checks the neighbours),
    /// then loose pairs.
    /// </summary>
    public IReadOnlyList<TrackCandidate> TrackCandidates()
    {
        var boxes = _candidates.Boxes;
        var list = new List<TrackCandidate>(_candidates.Pairs.Count + _candidates.CrowdPairs.Count + _candidates.LoosePairs.Count);
        foreach (var (t, b) in _candidates.Pairs)
            list.Add(new TrackCandidate(boxes[t], boxes[b], true));
        foreach (var crowd in _candidates.CrowdPairs)
            list.Add(new TrackCandidate(boxes[crowd.Top], boxes[crowd.Bottom], true));
        foreach (var (t, b) in _candidates.LoosePairs)
            list.Add(new TrackCandidate(boxes[t], boxes[b], false));
        return list;
    }

    /// <summary>Evaluates one candidate pair (boxes as in <see cref="NameplateCandidates.Boxes"/>), including the neighbour check of a crowd pair.</summary>
    public PairEvaluation Evaluate(Rect top, Rect bottom)
    {
        var own = Pair(top, bottom);
        if (!own.IsNameplate || !_crowd.TryGetValue((top, bottom), out var neighbours))
            return own;
        foreach (var line in neighbours)
        {
            var supported = false;
            if (_pairsOfLine.TryGetValue(line, out var others))
            {
                foreach (var (otherTop, otherBottom) in others)
                {
                    if (otherTop == top || otherTop == bottom || otherBottom == top || otherBottom == bottom)
                        continue;
                    if (Pair(otherTop, otherBottom).IsNameplate)
                    {
                        supported = true;
                        break;
                    }
                }
            }

            if (!supported)
                return PairEvaluation.No;
        }

        return own;
    }

    private PairEvaluation Pair(Rect top, Rect bottom)
    {
        if (!_memo.TryGetValue((top, bottom), out var evaluation))
            _memo[(top, bottom)] = evaluation = _detector.Evaluate(_frame, top, bottom);
        return evaluation;
    }

    private IEnumerable<(int Top, int Bottom)> AllPairs()
    {
        foreach (var pair in _candidates.Pairs)
            yield return pair;
        foreach (var crowd in _candidates.CrowdPairs)
            yield return (crowd.Top, crowd.Bottom);
        foreach (var pair in _candidates.LoosePairs)
            yield return pair;
    }

    private void Add(int line, (Rect Top, Rect Bottom) pair)
    {
        if (!_pairsOfLine.TryGetValue(line, out var list))
            _pairsOfLine[line] = list = new List<(Rect, Rect)>();
        list.Add(pair);
    }
}
