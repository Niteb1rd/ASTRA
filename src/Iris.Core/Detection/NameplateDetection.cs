using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>One nameplate found in a frame (one read of it: names are trusted only after the <see cref="NameplateTracker"/> confirmed them).</summary>
/// <param name="Name">The name as read by the letter model, upper case.</param>
/// <param name="Score">Sum of the log probabilities of the name's letters (closer to 0 is surer).</param>
/// <param name="NameBox">The name line, in frame pixels.</param>
/// <param name="DistanceBox">The distance line under it, in frame pixels.</param>
/// <param name="DistanceText">The distance line as read, such as <c>835.5m</c> or <c>9.0km</c> (digit look-alike letters already turned into digits).</param>
public sealed record NameplateDetection(string Name, double Score, Rect NameBox, Rect DistanceBox, string DistanceText)
{
    /// <summary>The score divided by the number of letters.</summary>
    public double MeanScore => Name.Length == 0 ? 0.0 : Score / Name.Length;
}

/// <summary>
/// Text lines found in a frame and the pairs that could be a nameplate: a name line with a line right below it, both isolated
/// (empty space on both sides) and centered on each other. <see cref="Pairs"/> hold indexes into <see cref="Boxes"/>.
/// <see cref="LoosePairs"/> pass every test except the isolation: they may only continue a track that is already a nameplate.
/// </summary>
public sealed record NameplateCandidates(IReadOnlyList<Rect> Boxes, IReadOnlyList<(int Top, int Bottom)> Pairs, IReadOnlyList<(int Top, int Bottom)> LoosePairs);

/// <summary>
/// What evaluating one candidate pair found out: <see cref="IsNameplate"/> (the lower line is a distance anchor and the upper line is
/// one word of name characters) and, when the name was also read surely enough, the <see cref="Detection"/>.
/// </summary>
public sealed record PairEvaluation(bool IsNameplate, NameplateDetection? Detection)
{
    public static PairEvaluation No { get; } = new(false, null);
}
