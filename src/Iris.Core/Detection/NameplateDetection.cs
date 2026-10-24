using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>One nameplate found in a frame (one read of it: names are trusted only after the <see cref="NameplateTracker"/> confirmed them).</summary>
/// <param name="Name">The name as read by the letter model, upper case. For a multi-row name the words of the rows joined, top to bottom, without a separator.</param>
/// <param name="Score">Sum of the log probabilities of the name's letters over all rows (closer to 0 is surer).</param>
/// <param name="NameBox">The name line (the LAST row of a multi-row name: the one right above the distance line), in frame pixels.</param>
/// <param name="DistanceBox">The distance line under it, in frame pixels.</param>
/// <param name="DistanceText">The distance line as read, such as <c>835.5m</c> or <c>9.0km</c> (digit look-alike letters already turned into digits).</param>
/// <param name="HasSpeedGroup">True when the distance line goes on with a speed readout opened by <c>[</c> (the plate is targeted: <c>9.0km [-29m/s]</c>); <see cref="DistanceBox"/> then covers the whole line, and the speed itself is not read.</param>
/// <param name="Rows">The boxes of the name rows, top to bottom, <paramref name="NameBox"/> last; one box for a name on one row. Null (a detection made by hand) means just <paramref name="NameBox"/>.</param>
/// <param name="Color">The colour class of the plate (<see cref="PlateColors"/>), null when there were too few text pixels to measure it.</param>
/// <param name="Hue">The hue (degrees) the colour class was decided on.</param>
/// <param name="Saturation">The saturation (0 to 1) the colour class was decided on.</param>
/// <param name="O0Evidence">For every O or 0 glyph of the name that the O/0 rule could measure: its index in <paramref name="Name"/> (rows joined) and P(O) before the verdict (see <see cref="Letters.O0Glyph"/>).
/// The tracker adds these up over the frames when <see cref="NameplateTrackerSettings.O0MultiFrame"/> is on. Null: none (the rule is off, or no glyph was a ring).</param>
public sealed record NameplateDetection(
    string Name,
    double Score,
    Rect NameBox,
    Rect DistanceBox,
    string DistanceText,
    bool HasSpeedGroup = false,
    IReadOnlyList<Rect>? Rows = null,
    PlateColor? Color = null,
    double Hue = 0.0,
    double Saturation = 0.0,
    IReadOnlyList<Letters.O0Glyph>? O0Evidence = null)
{
    /// <summary>The score divided by the number of letters.</summary>
    public double MeanScore => Name.Length == 0 ? 0.0 : Score / Name.Length;

    /// <summary>The name rows, top to bottom, <see cref="NameBox"/> last (never empty).</summary>
    public IReadOnlyList<Rect> NameRows => Rows is { Count: > 0 } ? Rows : new[] { NameBox };

    /// <summary>True when the name stands on more than one row.</summary>
    public bool IsMultiRow => Rows is { Count: > 1 };

    /// <summary>The whole plate: the union of the name rows and the distance line.</summary>
    public Rect PlateBox => PlateBoxes.PlateBox(NameRows, DistanceBox);
}

/// <summary>The rows a name stands on and what stands above them (see <see cref="NameplateDetector.FindCandidates"/>).</summary>
/// <param name="Rows">The rows of the plate, top to bottom, with the name line (the upper line of the pair) last; frame pixels. One element when nothing is stacked above.</param>
/// <param name="Blockers">Stacked lines above the top row that could not be joined to it: off-center ones, and a joinable row beyond <see cref="NameplateDetectorSettings.MaxRows"/>.
/// They only matter when they read as name words: the plate is then a partial stack, a nameplate without a name (see <see cref="NameplateDetector.Evaluate"/>).</param>
public sealed record PlateStack(IReadOnlyList<Rect> Rows, IReadOnlyList<Rect> Blockers);

/// <summary>Box arithmetic of a plate.</summary>
public static class PlateBoxes
{
    /// <summary>The smallest box that holds all the <paramref name="rows"/> and the <paramref name="distance"/> line.</summary>
    public static Rect PlateBox(IEnumerable<Rect> rows, Rect distance)
    {
        var left = distance.X;
        var top = distance.Y;
        var right = distance.X + distance.Width;
        var bottom = distance.Y + distance.Height;
        foreach (var row in rows)
        {
            left = Math.Min(left, row.X);
            top = Math.Min(top, row.Y);
            right = Math.Max(right, row.X + row.Width);
            bottom = Math.Max(bottom, row.Y + row.Height);
        }

        return new Rect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// A pair that is isolated except for text that belongs to other pairs (a crowd of nameplates side by side): the ink beside its lines lies inside
/// the boxes of the text lines <paramref name="Neighbours"/> (indexes into <see cref="NameplateCandidates.Boxes"/>), each of which is part of another
/// candidate pair. It counts as a strict pair once those neighbours read as real nameplates (see <see cref="FrameEvaluator"/>); clutter, HUD
/// arcs and panels beside a line never qualify, because their ink is not inside a line that pairs up.
/// </summary>
public readonly record struct CrowdPair(int Top, int Bottom, IReadOnlyList<int> Neighbours);

/// <summary>
/// Text lines found in a frame and the pairs that could be a nameplate: a name line with a line right below it, centered on each other.
/// <see cref="Pairs"/> have both lines isolated (empty space on both sides); <see cref="CrowdPairs"/> are isolated except for the text of other
/// pairs; <see cref="LoosePairs"/> pass every test except the isolation: they may only continue a track that is already a nameplate. All hold
/// indexes into <see cref="Boxes"/>.
/// </summary>
/// <param name="Boxes">The text lines (and the joined distance + speed lines after them), in frame pixels.</param>
/// <param name="Pairs">Strict pairs, as indexes into <paramref name="Boxes"/>.</param>
/// <param name="LoosePairs">Pairs that fail the isolation test.</param>
/// <param name="CrowdPairs">Pairs isolated except for the text of other pairs.</param>
/// <param name="Stacks">For the pairs whose name line has rows stacked above it (or a blocker above it): the rows and blockers, keyed by the (name line, line below) boxes; see <see cref="PlateStack"/>. Null: none.</param>
public sealed record NameplateCandidates(
    IReadOnlyList<Rect> Boxes,
    IReadOnlyList<(int Top, int Bottom)> Pairs,
    IReadOnlyList<(int Top, int Bottom)> LoosePairs,
    IReadOnlyList<CrowdPair> CrowdPairs,
    IReadOnlyDictionary<(Rect Top, Rect Bottom), PlateStack>? Stacks = null)
{
    /// <summary>The stack of a pair, or null when the name line has nothing stacked above it.</summary>
    public PlateStack? StackOf(Rect top, Rect bottom) => Stacks is not null && Stacks.TryGetValue((top, bottom), out var stack) ? stack : null;
}

/// <summary>
/// What evaluating one candidate pair found out: <see cref="IsNameplate"/> (the lower line is a distance anchor and the upper line is
/// one word of name characters) and, when the name was also read surely enough, the <see cref="Detection"/>.
/// </summary>
public sealed record PairEvaluation(bool IsNameplate, NameplateDetection? Detection)
{
    public static PairEvaluation No { get; } = new(false, null);
}
