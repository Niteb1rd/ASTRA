namespace Iris.Core.Detection;

/// <summary>
/// The tunable numbers of <see cref="NameplateDetector"/>. The defaults were tuned on gameplay footage at 3440x1440 (see the
/// README section "Phase 3, step 2: the nameplate detector and the replay tool"); every pixel size below is given for a frame
/// 1440 pixels high and is scaled with the real frame height (see <see cref="ReferenceHeight"/>).
/// </summary>
public sealed record NameplateDetectorSettings
{
    /// <summary>The settings IRIS ships with.</summary>
    public static NameplateDetectorSettings Default { get; } = new();

    /// <summary>
    /// Part of the frame that is searched, as fractions of its width and height. Nameplates float around the middle of the
    /// screen; the edges hold the HUD, which only makes noise. Defaults: x 15% to 85%, y 10% to 70%.
    /// </summary>
    public double RegionLeft { get; init; } = 0.15;

    public double RegionRight { get; init; } = 0.85;

    public double RegionTop { get; init; } = 0.10;

    public double RegionBottom { get; init; } = 0.70;

    /// <summary>
    /// Frame height for which the pixel constants of the detector (the 11 pixel top-hat window, letter heights of 10 to 22, line
    /// widths of 24 to 400, the 7 pixel join of letters, and the 4 and 6 pixel pairing tolerances) were tuned: 1440, the height of a
    /// 3440x1440 screen. For another frame height h every one of them is multiplied by h / ReferenceHeight (rounded, at least 1).
    /// The letter reader (<see cref="Letters.NameplateReader"/>) is NOT scaled: it enlarges every line crop 4 times and measures
    /// letters relative to their own capital height, so it copes with other sizes by itself, but its model was trained at 1440.
    /// </summary>
    public int ReferenceHeight { get; init; } = 1440;

    /// <summary>A handle has 4 to 25 characters (letters, digits, "-" and "_").</summary>
    public int MinNameLength { get; init; } = 4;

    public int MaxNameLength { get; init; } = 25;

    /// <summary>
    /// The name read must be at least this sure: the mean log probability per letter (closer to 0 is surer; clean reads are
    /// around -0.01, noise around -0.3 and below).
    /// </summary>
    public double MinNameScore { get; init; } = -0.20;

    /// <summary>
    /// A distance line that starts with a number but whose unit cannot be read (the unit letters are lower case in the game, which
    /// the model never saw) is accepted only for a name at least this sure ...
    /// </summary>
    public double PartialNameScore { get; init; } = -0.15;

    /// <summary>... and at least this long.</summary>
    public int PartialMinNameLength { get; init; } = 6;

    /// <summary>
    /// The distance line is read with the characters 0-9, K, M, "-" and "_" only. How far the model's own opinion may be from that
    /// (mean difference of the log probabilities over the first 6 pieces) before the line is called text, not a distance.
    /// </summary>
    public double MaxDistanceGap { get; init; } = 4.0;

    /// <summary>
    /// Characters glued to the unit of a distance ("1.9km" + more with no space) before the line counts as a speed line
    /// ("209m/s ..."), which is not a distance.
    /// </summary>
    public int MaxGluedAfterUnit { get; init; } = 6;

    /// <summary>
    /// Work budget per frame: at most this many different name lines (the upper lines of candidate pairs) are read with the letter
    /// model; further pairs are ignored. A deliberate difference from the Python reference, which has no such limit: a very noisy
    /// frame (explosions, bright clouds) can produce hundreds of candidate lines, and every read costs milliseconds. Real frames
    /// have a handful, so the default does not change the results on the parity fixtures.
    /// </summary>
    public int MaxCandidateTopsPerFrame { get; init; } = 24;

    /// <summary>A gap between two pieces of ink wider than this many capital heights is a space.</summary>
    public double WordGap { get; init; } = 0.33;

    /// <summary>
    /// A first piece of the distance line narrower than this many capital heights is taken for a digit: a "1" is a thin stroke
    /// that the model may call a K or an M.
    /// </summary>
    public double NarrowFirstPiece { get; init; } = 0.30;

    /// <summary>
    /// A pixel constant of the 1440 pixel high reference frame for a frame <paramref name="frameHeight"/> pixels high: scaled by
    /// frameHeight / <see cref="ReferenceHeight"/>, rounded half up, at least 1.
    /// </summary>
    public int Scale(int frameHeight, double value) =>
        Math.Max(1, (int)Math.Floor(value * frameHeight / ReferenceHeight + 0.5));

    /// <summary>Returns a description of the first problem with the values, or null if they are usable.</summary>
    public string? Validate()
    {
        if (!(RegionLeft >= 0 && RegionLeft < RegionRight && RegionRight <= 1))
            return "The scan region needs 0 <= left < right <= 1.";
        if (!(RegionTop >= 0 && RegionTop < RegionBottom && RegionBottom <= 1))
            return "The scan region needs 0 <= top < bottom <= 1.";
        if (ReferenceHeight <= 0)
            return "ReferenceHeight must be positive.";
        if (MinNameLength < 1 || MaxNameLength < MinNameLength)
            return "The name length limits are not usable.";
        if (MaxCandidateTopsPerFrame < 1)
            return "MaxCandidateTopsPerFrame must be at least 1.";
        return null;
    }
}
