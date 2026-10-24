namespace Iris.Core.Detection;

/// <summary>
/// The tunable numbers of <see cref="NameplateDetector"/>. The defaults were tuned on gameplay footage at 3440x1440 (see the
/// README section "Phase 3, step 2: the nameplate detector (v2), the tracker and the replay tool"); every pixel size below is given for a frame
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
    /// widths of 24 to 832 (<see cref="NameplateDetector.MaxLineWidth"/>), the 7 pixel join of letters, the pairing tolerances and the speck size) were tuned: 1440, the height of a
    /// 3440x1440 screen. For another frame height h every one of them is multiplied by h / ReferenceHeight (rounded, at least 1).
    /// The letter reader (<see cref="Letters.NameplateReader"/>) is NOT scaled: it enlarges every line crop 4 times and measures
    /// letters relative to their own capital height, so it copes with other sizes by itself, but its model was trained at 1440.
    /// </summary>
    public int ReferenceHeight { get; init; } = 1440;

    /// <summary>
    /// The frame height that the pixel constants are scaled with (see <see cref="ReferenceHeight"/>). 0, the default, means the height of the image
    /// given to the detector, which is right for a whole frame. The live loop gives the detector only a CROP of the screen (the scan region plus the
    /// <see cref="ContextPadding"/> around it) and sets this to the height of the game window, so that the constants are scaled by the real screen
    /// and not by the crop. Everything else (the scan region fractions) stays relative to the image given.
    /// </summary>
    public int ScaleHeight { get; init; }

    /// <summary>The height the pixel constants are scaled with for an image <paramref name="imageHeight"/> pixels high (<see cref="ScaleHeight"/> or the image's).</summary>
    public int EffectiveScaleHeight(int imageHeight) => ScaleHeight > 0 ? ScaleHeight : imageHeight;

    /// <summary>
    /// Pixels of context the cheap pass looks at beside the scan region (to see obstacles next to a line near the region's edge): three of the
    /// tallest letter lines plus 4 plus 2, scaled with <paramref name="scaleHeight"/> (the height of the whole frame or window). A caller that
    /// crops the frame itself must keep this much around the region, otherwise the lines at the region's edge are judged on less context than in a whole frame.
    /// </summary>
    public int ContextPadding(int scaleHeight) => 3 * Scale(scaleHeight, 22) + Scale(scaleHeight, 4) + 2;

    /// <summary>
    /// Pixels of context needed above and below the scan region: the colour mask's morphological kernel (about 11 pixels at the reference height) reads
    /// the pixels around each pixel, so a caller that crops must keep half a kernel plus a margin above and below the region to treat its edge like the rest of the frame (the padding is cheap, and harmless where the detector would not have needed it).
    /// </summary>
    public int VerticalContextPadding(int scaleHeight) => Scale(scaleHeight, 11) + 2;

    /// <summary>A name read from a nameplate has 4 to 64 characters (letters, digits, "-" and "_"); a read that starts or ends with "-" is no vote (<see cref="NameShape"/>).</summary>
    public int MinNameLength { get; init; } = 4;

    public int MaxNameLength { get; init; } = 64;

    /// <summary>
    /// The name read must be at least this sure to count as a vote: the mean log probability per letter (closer to 0 is surer; clean
    /// reads are around -0.01, noise around -0.3 and below).
    /// </summary>
    public double MinNameScore { get; init; } = -0.20;

    /// <summary>The distance read must be at least this sure: mean log probability per character.</summary>
    public double MinDistanceScore { get; init; } = -0.35;

    /// <summary>
    /// How far the model's own opinion may be from the restricted read of the distance line (mean difference of the log probabilities
    /// over the first 6 pieces) before the line is called text, not a distance.
    /// </summary>
    public double MaxDistanceGap { get; init; } = 4.0;

    /// <summary>
    /// Distance mode: a first or last piece of the line that the model reads with a log probability below this is a speck (noise next to
    /// the line, a star, a bit of a neighbor) and is dropped before the line is judged.
    /// </summary>
    public double DistanceEndScore { get; init; } = -1.0;

    /// <summary>The number in front of the unit may be at most this (0 to 1000).</summary>
    public int MaxDistanceValue { get; init; } = 1000;

    /// <summary>
    /// Distance mode: a piece of ink with less ink than this fraction of (capital height)^2 is noise. Much smaller than the limit for names
    /// (<see cref="Letters.NameplateReader.NameMinInk"/>, 0.02) because the period of "5.8km" is a tiny speck that must survive.
    /// </summary>
    public double DistanceMinInk { get; init; } = 0.003;

    /// <summary>A gap between two pieces of ink wider than this many capital heights is a space.</summary>
    public double WordGap { get; init; } = 0.33;

    /// <summary>
    /// A line (the name and the distance) counts as isolated when there is no ink within this many line heights to its left and right
    /// (a line height is the height of the line's bounding box, about one character width). Text that is part of a longer text, a sign or a panel fails this.
    /// </summary>
    public double ClearLines { get; init; } = 3.0;

    /// <summary>
    /// The crowd exception: ink beside a line does not break its isolation when it is inside the box of another text line that is itself part of a
    /// candidate pair, and that pair reads as a real nameplate (a name over a valid distance). Nameplates drift close together; HUD clutter and
    /// multi-word text beside a line still reject it.
    /// </summary>
    public bool CrowdException { get; init; } = true;

    /// <summary>Shapes of at most this many pixels (at 1440 high) are specks (stars, noise), not obstacles for the isolation check.</summary>
    public double SpeckArea { get; init; } = 4.0;

    /// <summary>The centers of the name and the distance line may differ by this many pixels (at 1440 high).</summary>
    public double CenterTolerance { get; init; } = 5.0;

    /// <summary>
    /// A pair is a left aligned label column (HUD: "R-ALT" over "VSI"), not a centered nameplate, when the left edges are equal within
    /// <see cref="LeftTolerance"/> pixels while the widths differ by at least <see cref="LeftWidthDifference"/> pixels (both at 1440 high).
    /// </summary>
    public double LeftTolerance { get; init; } = 2.0;

    public double LeftWidthDifference { get; init; } = 8.0;

    /// <summary>
    /// MULTI-ROW NAMES: the game wraps a long handle onto up to this many stacked rows (the last row is the name line right above the distance line, centered on it like
    /// every other row). A 4th row above the chain is not joined: it blocks the plate (a nameplate without a name) when it reads as a name word.
    /// </summary>
    public int MaxRows { get; init; } = 3;

    /// <summary>Rows: the empty gap between two rows is at least 1 pixel and at most this many line heights (of the lower row).</summary>
    public double RowGapMax { get; init; } = 0.8;

    /// <summary>Rows: the distance from the top of a row to the top of the row below it is at least this many line heights (the game: 1.19 to 1.46, mean 1.36).</summary>
    public double RowPitchMin { get; init; } = 1.05;

    /// <summary>... and at most this many.</summary>
    public double RowPitchMax { get; init; } = 1.8;

    /// <summary>
    /// A row above the bottom row of a stack is a FULL row: the game wraps names at a fixed width (about 14.6 line heights, measured 196 to 208 px at 1440 high),
    /// so it is at least this many line heights wide. A short line above a name is no wrapped row.
    /// </summary>
    public double RowMinCaps { get; init; } = 11.0;

    /// <summary>
    /// The sub-pixel O/0 rule (<see cref="Letters.O0Rule"/>): every O or 0 the model read in a NAME is measured on its own glyph and replaced by the verdict when the rule is sure
    /// (P at least 0.95 for O, at most 0.05 for 0). On by default: on the synthetic detector frames it changes no read.
    /// </summary>
    public bool SubPixelO0Rule { get; init; } = true;

    /// <summary>
    /// The underscore / hyphen rule (<see cref="Letters.UnderscoreHyphenRule"/>): every <c>_</c> or <c>-</c> the model read in a NAME is measured on the reader's own mask and replaced by the
    /// verdict of its height in the capital band (an underscore sits at the bottom of the band, a hyphen in the middle; a bar in the undecided band in between keeps the model's character).
    /// On by default: on the 19 training clips it changes no confirmed name and fixes every measured underscore read as a hyphen (16 of 16 single frames of the test character).
    /// It runs after the O/0 rule, per row of a multi-row name, and never on a distance line. Off: the model's bars as read.
    /// </summary>
    public bool UnderscoreHyphenRule { get; init; } = true;

    /// <summary>The thresholds of the plate colour classes (see <see cref="PlateColors"/>).</summary>
    public PlateColorSettings PlateColorRules { get; init; } = PlateColorSettings.Default;

    /// <summary>
    /// A targeted nameplate prints its closing speed after the distance, <c>9.0km [-29m/s]</c>. When the line joiner leaves the speed group as a
    /// separate box, the box to the right of a line within this many line heights (same row) is joined to it as one candidate line.
    /// </summary>
    public double SpeedJoinGap { get; init; } = 1.5;

    /// <summary>... and it must be at most this many line heights wide.</summary>
    public double SpeedJoinMaxLines { get; init; } = 8.0;

    /// <summary>
    /// The piece after the word gap that follows the unit is the opening bracket "[" of the speed group when the model's best character
    /// (among all of its characters) is "[" with at least this log probability.
    /// </summary>
    public double BracketMinScore { get; init; } = -0.7;

    /// <summary>
    /// Work budget per frame (single frame <see cref="NameplateDetector.Detect"/>): at most this many different name lines (the upper
    /// lines of candidate pairs) are evaluated; further pairs are ignored. The budget is soft: the neighbour check of a crowd
    /// pair (<see cref="FrameEvaluator"/>) may read the pairs of neighbouring lines, which are not counted, so a frame can read more
    /// pairs than this. The tracker has its own budget (<see cref="NameplateTrackerSettings.MaxEvaluationsPerFrame"/>), soft in the same way.
    /// </summary>
    public int MaxCandidateTopsPerFrame { get; init; } = 24;

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
        if (ScaleHeight < 0)
            return "ScaleHeight must not be negative (0 = the height of the image).";
        if (MinNameLength < 1 || MaxNameLength < MinNameLength)
            return "The name length limits are not usable.";
        if (MaxCandidateTopsPerFrame < 1)
            return "MaxCandidateTopsPerFrame must be at least 1.";
        if (ClearLines < 0 || SpeckArea < 0 || CenterTolerance < 0 || LeftTolerance < 0 || LeftWidthDifference < 0 || DistanceMinInk < 0)
            return "The isolation and pairing numbers must not be negative.";
        if (SpeedJoinGap < 0 || SpeedJoinMaxLines < 0)
            return "The speed group join limits must not be negative.";
        if (MaxRows < 1 || RowGapMax < 0 || RowPitchMin < 0 || RowPitchMax < RowPitchMin || RowMinCaps < 0)
            return "The row limits are not usable (at least 1 row, positive gap and pitch, maximum pitch at least the minimum).";
        if (MaxDistanceValue < 0)
            return "MaxDistanceValue must not be negative.";
        return null;
    }
}
