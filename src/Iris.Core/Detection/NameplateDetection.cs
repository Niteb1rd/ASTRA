using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>One nameplate found in a frame.</summary>
/// <param name="Name">The name as read by the letter model, upper case.</param>
/// <param name="Score">Sum of the log probabilities of the name's letters (closer to 0 is surer).</param>
/// <param name="NameBox">The name line, in frame pixels.</param>
/// <param name="DistanceBox">The line below it, in frame pixels (for a stacked nameplate: the line of the nameplate below).</param>
/// <param name="DistanceText">What the model read in the distance line (digits, K, M, "-" and "_" only; it is a check, not a
/// measurement); empty for a stacked nameplate.</param>
/// <param name="Stacked">True when the name sits directly above another confirmed nameplate and its own distance line is hidden.</param>
public sealed record NameplateDetection(string Name, double Score, Rect NameBox, Rect DistanceBox, string DistanceText, bool Stacked)
{
    /// <summary>The score divided by the number of letters.</summary>
    public double MeanScore => Name.Length == 0 ? 0.0 : Score / Name.Length;
}

/// <summary>
/// Text lines found in a frame and the pairs that could be a nameplate (a name line with a centered line right below it).
/// <see cref="Pairs"/> hold indexes into <see cref="Boxes"/>.
/// </summary>
public sealed record NameplateCandidates(IReadOnlyList<Rect> Boxes, IReadOnlyList<(int Top, int Bottom)> Pairs);
