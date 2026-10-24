using System.Runtime.InteropServices;
using Iris.Core.Detection.Letters;
using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>
/// Finds nameplates in a game frame and reads their names, with the trained letter model only (no Tesseract). Version 2 of the
/// detector: false positives are worse than misses (a name can be read again a moment later), so a nameplate must pass strict tests.
/// <list type="number">
/// <item><b>Cheap pass</b> (<see cref="FindCandidates"/>). Crop to the scan region (<see cref="NameplateDetectorSettings"/>). Color mask: a pixel is
/// text when it is locally brighter than its 11x11 surroundings (top-hat of its channel) AND cyan or white, red (hostile) or green
/// (friendly) in hue. Drop shapes taller than a letter, join the letters of a word with a 7x1 dilation, keep the connected shapes that look
/// like a text line (10 to 22 pixels high, 24 to <see cref="MaxLineWidth"/> = 832 wide, partly filled). A line is ISOLATED when there is no ink (other than specks) within
/// <see cref="NameplateDetectorSettings.ClearLines"/> line heights to its left and right. A pair is an isolated line directly above another
/// isolated line (gap up to 1.4 line heights), about as high, centered on it, and not a left aligned label column. A targeted nameplate prints
/// its speed after the distance (<c>9.0km [-29m/s]</c>); the whole line is centered under the name. When the line joiner leaves the speed group as a
/// box of its own, the two boxes are also offered as one joined line (<see cref="SpeedUnions"/>), which can only be the lower line of a pair.</item>
/// <item><b>Evaluate</b> (<see cref="Evaluate"/>). The lower line is read in DISTANCE MODE (the characters 0-9 . k m and the digit look-alikes;
/// the period is not dropped as noise) and must be exactly <c>number.digit</c> + <c>m</c> or <c>km</c>, see <see cref="DistanceLineRule"/>: the
/// ANCHOR (one word gap after the unit may be followed by the <c>[</c> of a speed group, which is then ignored). Only then the upper line is read with the name alphabet; it must be ONE word of 4 to 64 characters A-Z 0-9 - _ that does not start or end with - (such a read is a nameplate without a vote; "_" may stand anywhere). If it is also read
/// surely enough (score per letter at least <see cref="NameplateDetectorSettings.MinNameScore"/>) the evaluation carries a
/// <see cref="NameplateDetection"/>, which is one vote for that name.</item>
/// <item><b>Multi-row names</b>. The game wraps a long handle onto up to three centered rows above the distance line. In the cheap pass every pair also gets the rows
/// stacked above its name line (<see cref="FindStacks"/>, a port of <c>stack_rows</c> of the Python reference): a row is stacked when the gap to the row below is 1 pixel to
/// <see cref="NameplateDetectorSettings.RowGapMax"/> line heights, the top-to-top pitch is <see cref="NameplateDetectorSettings.RowPitchMin"/> to
/// <see cref="NameplateDetectorSettings.RowPitchMax"/> line heights, the heights match, the row is at least <see cref="NameplateDetectorSettings.RowMinCaps"/> line heights wide
/// (every row above the last is a full row) and the x ranges overlap; it is joinable when it is also centered on the distance line (within the center tolerance). The chain goes up
/// while a joinable row exists, at most <see cref="NameplateDetectorSettings.MaxRows"/> rows. <see cref="Evaluate"/> reads every row as ONE word and joins the words into the name (4 to 64
/// characters, no <c>-</c> at either end); a row that is not a word of name characters drops the stack (the name line is read alone, as before); a name-like line above the top row
/// that cannot join (off center, or a 4th row) makes the plate a nameplate without a name. The detection carries the row boxes, the whole plate box and the plate colour
/// (<see cref="PlateColors"/>).</item>
/// <item><b>Track</b> (<see cref="NameplateTracker"/>). Reads are not trusted one by one: the tracker confirms a name after it was read identically
/// several times and does not re-read what it has judged not to be a nameplate.</item>
/// </list>
/// All pixel constants are for 1440 pixel high frames and scale with the real frame height (see
/// <see cref="NameplateDetectorSettings.ReferenceHeight"/>). The detector reuses its image buffers between frames, so one instance
/// handles one frame at a time (it is not thread safe); give each thread its own, they can share the <see cref="NameplateReader"/>
/// and its model. Dispose it when done.
/// </summary>
public sealed class NameplateDetector : IDisposable
{
    // Top-hat thresholds and color rules of the text mask (0 to 255 intensities, not pixel sizes: not scaled).
    private const double TopHatMin = 28;
    private const double GreenTopHatMin = 20;

    /// <summary>
    /// The widest text line, in pixels of a frame 1440 high (scaled with the frame height), that the cheap pass accepts: 832 = 64 characters (the
    /// longest handle) at 13 px each. A letter advances 10.1 px on average at 1440 (measured on 2801 reads of labeled names of 6 or more letters:
    /// median 10.0, 95th percentile 11.0, widest 11.7, the same at every line height from 11 to 22 px), so a 64 character name is about 650 px wide
    /// and 750 px even with only wide letters; 832 leaves 11 % over the widest advance seen. It was 400 (about 40 characters).
    /// </summary>
    public const int MaxLineWidth = 832;

    /// <summary>The characters a name is read with.</summary>
    public const string NameAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";

    private readonly NameplateReader _reader;
    private readonly NameplateDetectorSettings _settings;
    private readonly bool[] _distanceAlphabet;
    private readonly bool[] _nameAlphabet;
    private readonly Dictionary<(int Width, int Height), Mat> _kernels = new();

    // Image buffers reused from frame to frame.
    private readonly Mat _blue = new();
    private readonly Mat _green = new();
    private readonly Mat _red = new();
    private readonly Mat _hat = new();
    private readonly Mat _cyanWhite = new();
    private readonly Mat _redMask = new();
    private readonly Mat _greenMask = new();
    private readonly Mat _scratch = new();
    private readonly Mat _mask = new();
    private readonly Mat _kept = new();
    private readonly Mat _obstacle = new();
    private readonly Mat _labelHit = new(); // scratch: pixels of one component inside its bounding box
    private readonly Mat _joined = new();
    private readonly Mat _labels = new();
    private readonly Mat _stats = new();
    private readonly Mat _centroids = new();
    private int[] _statsBuffer = Array.Empty<int>();
    private bool _disposed;

    public NameplateDetector(NameplateReader reader, NameplateDetectorSettings? settings = null)
    {
        _reader = reader;
        _settings = settings ?? NameplateDetectorSettings.Default;
        var problem = _settings.Validate();
        if (problem is not null)
            throw new ArgumentException(problem, nameof(settings));
        _distanceAlphabet = reader.CreateAlphabet(DistanceLineRule.Alphabet);
        _nameAlphabet = reader.CreateAlphabet(NameAlphabet);
    }

    /// <summary>
    /// Finds the nameplates in one BGR, 8 bit, 3 channel frame, without tracking: every candidate pair is evaluated and every pair that
    /// passes with a sure enough name is reported (a single read; use <see cref="NameplateTracker"/> for trusted names). The frame is
    /// only read. An empty list when there are none. At most <see cref="NameplateDetectorSettings.MaxCandidateTopsPerFrame"/> different name
    /// lines are evaluated per frame.
    /// </summary>
    public IReadOnlyList<NameplateDetection> Detect(Mat bgr)
    {
        var candidates = FindCandidates(bgr);
        var evaluator = CreateEvaluator(bgr, candidates);
        var found = new List<NameplateDetection>();
        var confirmed = new HashSet<int>();
        var tops = new HashSet<int>();
        foreach (var (top, bottom) in candidates.Pairs.Concat(candidates.CrowdPairs.Select(c => (c.Top, c.Bottom))))
        {
            if (confirmed.Contains(top))
                continue;
            if (!tops.Contains(top) && tops.Count >= _settings.MaxCandidateTopsPerFrame)
                continue;
            tops.Add(top);
            var evaluation = evaluator.Evaluate(candidates.Boxes[top], candidates.Boxes[bottom]);
            if (evaluation.IsNameplate && evaluation.Detection is not null)
            {
                found.Add(evaluation.Detection);
                confirmed.Add(top);
            }
        }

        return found;
    }

    /// <summary>
    /// An evaluator for the candidate pairs of one frame (see <see cref="FrameEvaluator"/>): strict pairs are evaluated with
    /// <see cref="Evaluate"/>, crowd pairs also need the nameplates beside them to be real. The frame must stay unchanged while it is used.
    /// </summary>
    public FrameEvaluator CreateEvaluator(Mat bgr, NameplateCandidates candidates) => new(this, bgr, candidates);

    /// <summary>
    /// The expensive part for one candidate pair (the boxes come from <see cref="FindCandidates"/>): reads the lower line as a distance
    /// anchor, then the upper line(s) as a name. See the class remarks. <paramref name="stack"/> is <see cref="NameplateCandidates.StackOf"/> of the
    /// pair: the rows of a multi-row name (the last one is <paramref name="top"/>) and the lines that could not join them; null: a single row. Port of <c>evaluate_pair</c>.
    /// </summary>
    public PairEvaluation Evaluate(Mat bgr, Rect top, Rect bottom, PlateStack? stack = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var distance = ReadDistance(bgr, bottom);
        if (distance is null)
            return PairEvaluation.No;

        // the name line itself is always read (and must be a word); the rows above it only when there is a stack
        var nameLine = ReadNameWord(bgr, top);
        if (nameLine is null)
            return PairEvaluation.No; // a stack made of words needs its last row to be one too, and a single row needs it anyway

        IReadOnlyList<Rect> rows = new[] { top };
        IReadOnlyList<Rect> blockers = stack?.Blockers ?? Array.Empty<Rect>();
        var words = new List<NameWord>();
        if (stack is not null && stack.Rows.Count > 1 && stack.Rows[stack.Rows.Count - 1] == top)
        {
            var ok = true;
            for (var i = 0; i < stack.Rows.Count - 1 && ok; i++)
            {
                var word = ReadNameWord(bgr, stack.Rows[i]);
                if (word is null)
                    ok = false;
                else
                    words.Add(word);
            }

            if (ok)
            {
                rows = stack.Rows;
            }
            else
            {
                words.Clear(); // the stack is not made of words: the name line alone, exactly as before multi-row names
                blockers = Array.Empty<Rect>();
            }
        }

        words.Add(nameLine);
        var text = string.Concat(words.Select(w => w.Text));
        var score = 0.0;
        foreach (var word in words)
            score += word.Score;
        if (text.Length < _settings.MinNameLength || text.Length > _settings.MaxNameLength)
            return PairEvaluation.No;

        // a name-like line above the top row that does not join (off center, or a 4th row): a partial stack, a nameplate but no name
        foreach (var blocker in blockers)
        {
            if (ReadNameWord(bgr, blocker) is not null && ReadDistance(bgr, blocker) is null)
                return new PairEvaluation(true, null);
        }

        if (!NameShape.HasValidEdges(text))
            return new PairEvaluation(true, null); // a nameplate, but a name may not start or end with -: no vote
        if (score / text.Length < _settings.MinNameScore)
            return new PairEvaluation(true, null); // a nameplate, but this read is too unsure to vote

        // the O/0 measurements of every row, indexed in the joined name (the evidence of the tracker's O0MultiFrame rule)
        List<O0Glyph>? evidence = null;
        var offset = 0;
        foreach (var word in words)
        {
            foreach (var glyph in word.O0)
                (evidence ??= new List<O0Glyph>()).Add(new O0Glyph(offset + glyph.Index, glyph.ProbabilityO));
            offset += word.Text.Length;
        }

        var color = PlateColors.Classify(words.Select(w => w.Mask), _settings.PlateColorRules);
        return new PairEvaluation(true, new NameplateDetection(
            text, score, top, bottom, distance.Value.Text, distance.Value.HasSpeedGroup,
            rows, color?.Color, color?.Hue ?? 0.0, color?.Saturation ?? 0.0, evidence));
    }

    /// <summary>One row of a name read as one word: the characters, the sum of their log probabilities, the line mask (with the colours of its ink) and the O/0 measurements (indexes in this row's text).</summary>
    private sealed record NameWord(string Text, double Score, LineMask Mask, IReadOnlyList<O0Glyph> O0);

    /// <summary>
    /// The line in <paramref name="box"/> read with the name alphabet, when it is ONE word of name characters (A-Z, 0-9, <c>-</c>, <c>_</c>); otherwise null
    /// (<c>_name_word</c> of the Python reference). No length limit: a row of a multi-row name may be short.
    /// </summary>
    private NameWord? ReadNameWord(Mat bgr, Rect box)
    {
        var mask = NameplateFeatures.CreateMask(bgr, box, keepColors: true);
        if (mask is null)
            return null;
        var read = _reader.ReadPieces(mask, _nameAlphabet);
        if (read.Pieces.Count == 0 || !IsOneWord(read))
            return null;
        var text = string.Concat(read.Pieces.Select(p => p.Text));
        if (text.Length == 0)
            return null;
        foreach (var ch in text)
            if (!(ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return null;
        var score = 0.0;
        foreach (var piece in read.Pieces)
            score += piece.Score;
        IReadOnlyList<O0Glyph> evidence = Array.Empty<O0Glyph>();
        if (_settings.SubPixelO0Rule)
        {
            var measured = new List<O0Glyph>();
            text = O0Rule.Correct(bgr, read, text, measured); // the letter O or the digit 0: measured on the glyph's own pixels (names only, never distance lines)
            evidence = measured;
        }

        if (_settings.UnderscoreHyphenRule)
            text = UnderscoreHyphenRule.Correct(read, text); // an underscore or a hyphen: told apart by the height of the bar in the capital band (after the O/0 pass, per row)
        return new NameWord(text, score, mask, evidence);
    }

    /// <summary>The line in <paramref name="box"/> read in distance mode, when it is a distance anchor (<see cref="DistanceLineRule"/>); otherwise null.</summary>
    private DistanceReading? ReadDistance(Mat bgr, Rect box)
    {
        var mask = NameplateFeatures.CreateMask(bgr, box);
        if (mask is null)
            return null;
        var read = _reader.ReadPieces(mask, _distanceAlphabet, _settings.DistanceMinInk);
        return DistanceLineRule.Evaluate(read, _settings, piece => _reader.IsOpenBracket(mask, piece.Start, piece.End, _settings.BracketMinScore));
    }

    /// <summary>
    /// The cheap pass only: the text lines of the frame and the pairs that could be nameplates. No reading. Public so that the tracker driver,
    /// tests and tools can look at the geometry on its own.
    /// </summary>
    public NameplateCandidates FindCandidates(Mat bgr)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));

        var width = bgr.Width;
        var height = bgr.Height;
        // the pixel constants follow the height of the whole screen when the caller gives a crop (see NameplateDetectorSettings.ScaleHeight)
        var scaleHeight = _settings.EffectiveScaleHeight(height);
        // same arithmetic as the Python reference: int(fraction * size), truncating
        var x0 = (int)(_settings.RegionLeft * width);
        var x1 = Math.Min(width, (int)(_settings.RegionRight * width));
        var y0 = (int)(_settings.RegionTop * height);
        var y1 = Math.Min(height, (int)(_settings.RegionBottom * height));
        var empty = new NameplateCandidates(Array.Empty<Rect>(), Array.Empty<(int, int)>(), Array.Empty<(int, int)>(), Array.Empty<CrowdPair>());
        if (x1 <= x0 || y1 <= y0)
            return empty;

        // The mask is made a little wider than the region (room to look for obstacles beside lines near the region's edge);
        // only lines that lie completely inside the region itself are candidates.
        var pad = _settings.ContextPadding(scaleHeight);
        var px0 = Math.Max(0, x0 - pad);
        var px1 = Math.Min(width, x1 + pad);

        using var region = new Mat(bgr, new Rect(px0, y0, px1 - px0, y1 - y0));
        BuildColorMask(region, ScaledOdd(scaleHeight, 11));
        var lines = FindTextLines(scaleHeight);
        if (lines.Count == 0)
            return empty;

        // keep the lines inside the nominal region (x of the mask + px0 is the frame position)
        var local = new List<Rect>();
        foreach (var line in lines)
            if (x0 <= line.X + px0 && line.X + line.Width + px0 <= x1)
                local.Add(line);
        if (local.Count == 0)
            return empty;

        // lines whose speed readout the line joiner left apart (a gap wider than its 7 pixels after the unit) are also offered as ONE line
        // (distance + speed group); these joined boxes come after the real lines and can only be the lower line of a pair
        var realCount = local.Count;
        local.AddRange(SpeedUnions(local, scaleHeight));

        var clear = new bool[local.Count];
        for (var i = 0; i < clear.Length; i++)
            clear[i] = IsClear(local[i], scaleHeight);

        var allPairs = PairLines(local, scaleHeight, realCount);
        var stacks = FindStacks(local, allPairs, realCount, scaleHeight);
        var (pairs, loose, crowd) = SortPairs(local, clear, allPairs, realCount);
        var boxes = new Rect[local.Count];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new Rect(local[i].X + px0, local[i].Y + y0, local[i].Width, local[i].Height);
        Rect Shift(Rect r) => new(r.X + px0, r.Y + y0, r.Width, r.Height);
        Dictionary<(Rect Top, Rect Bottom), PlateStack>? shifted = null;
        if (stacks.Count > 0)
        {
            shifted = new Dictionary<(Rect Top, Rect Bottom), PlateStack>();
            foreach (var ((t, b), (rows, blockers)) in stacks)
                shifted[(Shift(t), Shift(b))] = new PlateStack(rows.Select(Shift).ToList(), blockers.Select(Shift).ToList());
        }

        return new NameplateCandidates(boxes, pairs, loose, crowd, shifted);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mat in new[] { _blue, _green, _red, _hat, _cyanWhite, _redMask, _greenMask, _scratch, _mask, _kept, _obstacle, _labelHit, _joined, _labels, _stats, _centroids })
            mat.Dispose();
        foreach (var kernel in _kernels.Values)
            kernel.Dispose();
        _kernels.Clear();
    }

    // ------------------------------------------------------------------------------------------------ scaling

    private int Scaled(int frameHeight, double value) => _settings.Scale(frameHeight, value);

    private int ScaledOdd(int frameHeight, double value)
    {
        var k = Scaled(frameHeight, value);
        return k % 2 == 1 ? k : k + 1;
    }

    private Mat Kernel(int width, int height)
    {
        if (!_kernels.TryGetValue((width, height), out var kernel))
        {
            kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(width, height));
            _kernels[(width, height)] = kernel;
        }

        return kernel;
    }

    // ------------------------------------------------------------------------------------------------ stage 1: color mask

    /// <summary>
    /// Text pixels of the region into <see cref="_mask"/> (255 text, 0 not): locally bright (top-hat of the channel) AND
    /// cyan/white, red or green. All comparisons are on 8 bit values; "a - b > t" is a saturating subtraction and a threshold,
    /// which is the same as the 16 bit arithmetic of the Python reference.
    /// </summary>
    private void BuildColorMask(Mat region, int tophat)
    {
        Cv2.ExtractChannel(region, _blue, 0);
        Cv2.ExtractChannel(region, _green, 1);
        Cv2.ExtractChannel(region, _red, 2);
        var kernel = Kernel(tophat, tophat);

        // cyan / white: th_b > 28 and b > 150 and g > 140 and b >= r
        Cv2.MorphologyEx(_blue, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _cyanWhite, TopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_blue, _scratch, 150, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
        Cv2.Threshold(_green, _scratch, 140, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
        Cv2.Subtract(_red, _blue, _scratch); // 0 exactly when r <= b
        Cv2.Threshold(_scratch, _scratch, 0, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);

        // red: th_r > 28 and r > 140 and r - g > 80 and r - b > 60
        Cv2.MorphologyEx(_red, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _redMask, TopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_red, _scratch, 140, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
        Cv2.Subtract(_red, _green, _scratch);
        Cv2.Threshold(_scratch, _scratch, 80, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);
        Cv2.Subtract(_red, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, 60, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_redMask, _scratch, _redMask);

        // friendly green (about RGB 77, 181, 78): th_g > 20 and g > 90 and g - r > 35 and g - b > 30
        Cv2.MorphologyEx(_green, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _greenMask, GreenTopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_green, _scratch, 90, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
        Cv2.Subtract(_green, _red, _scratch);
        Cv2.Threshold(_scratch, _scratch, 35, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);
        Cv2.Subtract(_green, _blue, _scratch);
        Cv2.Threshold(_scratch, _scratch, 30, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_greenMask, _scratch, _greenMask);

        Cv2.BitwiseOr(_cyanWhite, _redMask, _mask);
        Cv2.BitwiseOr(_mask, _greenMask, _mask);
    }

    // ------------------------------------------------------------------------------------------------ stage 2: text lines and obstacles

    /// <summary>
    /// Boxes (in mask pixels) of the text lines of <see cref="_mask"/>: shapes taller than a letter are removed, the letters
    /// of a word are joined by a wide, flat dilation, and a joined shape is a line when its size is right and it is partly,
    /// but not mostly, filled (text, not a solid bar or a hairline). Also builds <see cref="_obstacle"/>: the mask without its specks
    /// (shapes of at most <see cref="NameplateDetectorSettings.SpeckArea"/> pixels), for the isolation check.
    /// </summary>
    private List<Rect> FindTextLines(int frameHeight)
    {
        var maxHeight = Scaled(frameHeight, 22);
        var minHeight = Scaled(frameHeight, 10);
        var minWidth = Scaled(frameHeight, 24);
        var maxWidth = Scaled(frameHeight, MaxLineWidth);
        var speck = Math.Max(1, (int)Math.Floor(_settings.SpeckArea * Math.Pow(frameHeight / (double)_settings.ReferenceHeight, 2) + 0.5));

        var boxes = new List<Rect>();
        var count = Cv2.ConnectedComponentsWithStats(_mask, _labels, _stats, _centroids, PixelConnectivity.Connectivity8);
        if (count <= 1)
            return boxes; // nothing in the mask: no lines, and nobody asks for the obstacles

        if (_labels.Type() != MatType.CV_32SC1)
            throw new InvalidOperationException($"Unexpected label image type {_labels.Type()}.");
        CopyStats(count);

        var anyTall = false;
        var anySpeck = false;
        for (var i = 1; i < count; i++)
        {
            if (_statsBuffer[i * 5 + 3] > maxHeight)
                anyTall = true;
            if (_statsBuffer[i * 5 + 4] <= speck)
                anySpeck = true;
        }

        // Obstacles: every shape that is not a speck, whatever its height. Same result as Python's "big[labels]": copy the mask, then
        // clear every speck inside its bounding box (the label image says which pixels of the box belong to it).
        _mask.CopyTo(_obstacle);
        if (anySpeck)
            for (var i = 1; i < count; i++)
                if (_statsBuffer[i * 5 + 4] <= speck)
                    ClearComponent(_obstacle, i);

        // Text candidates: the mask without the shapes taller than a letter (reticle arcs, frames).
        Mat kept;
        if (!anyTall)
        {
            kept = _mask;
        }
        else
        {
            _mask.CopyTo(_kept);
            for (var i = 1; i < count; i++)
                if (_statsBuffer[i * 5 + 3] > maxHeight)
                    ClearComponent(_kept, i);
            kept = _kept;
        }

        Cv2.Dilate(kept, _joined, Kernel(Scaled(frameHeight, 7), 1));
        count = Cv2.ConnectedComponentsWithStats(_joined, _labels, _stats, _centroids, PixelConnectivity.Connectivity8);
        CopyStats(count);
        for (var i = 1; i < count; i++)
        {
            var x = _statsBuffer[i * 5 + 0];
            var y = _statsBuffer[i * 5 + 1];
            var w = _statsBuffer[i * 5 + 2];
            var h = _statsBuffer[i * 5 + 3];
            if (h < minHeight || h > maxHeight || w < minWidth || w > maxWidth || !(w > h * 1.5))
                continue;
            using var roi = new Mat(kept, new Rect(x, y, w, h));
            var fill = Cv2.CountNonZero(roi) / (double)(w * h);
            if (fill > 0.12 && fill < 0.75)
                boxes.Add(new Rect(x, y, w, h));
        }

        return boxes;
    }

    /// <summary>Clears the pixels of component <paramref name="label"/> of <see cref="_labels"/> in <paramref name="target"/> (inside the component's bounding box).</summary>
    private void ClearComponent(Mat target, int label)
    {
        var box = new Rect(_statsBuffer[label * 5 + 0], _statsBuffer[label * 5 + 1], _statsBuffer[label * 5 + 2], _statsBuffer[label * 5 + 3]);
        using var labelsRoi = new Mat(_labels, box);
        using var targetRoi = new Mat(target, box);
        Cv2.Compare(labelsRoi, new Scalar(label), _labelHit, CmpType.EQ);
        targetRoi.SetTo(Scalar.All(0), _labelHit);
    }

    private void CopyStats(int count)
    {
        var values = count * 5;
        if (_statsBuffer.Length < values)
            _statsBuffer = new int[values];
        Marshal.Copy(_stats.Data, _statsBuffer, 0, values);
    }

    /// <summary>
    /// True when the line (a box in mask pixels) has no obstacle within <see cref="NameplateDetectorSettings.ClearLines"/> line heights to
    /// its left and to its right, in the rows of the line.
    /// </summary>
    private bool IsClear(Rect box, int frameHeight)
    {
        var reach = (int)Math.Ceiling(_settings.ClearLines * box.Height);
        var maskWidth = _obstacle.Cols;
        var maskHeight = _obstacle.Rows;
        var rowStart = Math.Max(0, box.Y);
        var rowEnd = Math.Min(maskHeight, box.Y + box.Height);
        if (rowEnd <= rowStart)
            return true;

        var leftStart = Math.Max(0, box.X - reach);
        var leftEnd = box.X;
        if (leftEnd > leftStart && HasInk(new Rect(leftStart, rowStart, leftEnd - leftStart, rowEnd - rowStart)))
            return false;

        var rightStart = box.X + box.Width;
        var rightEnd = Math.Min(maskWidth, box.X + box.Width + reach);
        if (rightEnd > rightStart && HasInk(new Rect(rightStart, rowStart, rightEnd - rightStart, rowEnd - rowStart)))
            return false;
        return true;
    }

    private bool HasInk(Rect area)
    {
        using var roi = new Mat(_obstacle, area);
        return Cv2.CountNonZero(roi) > 0;
    }

    /// <summary>
    /// Candidate "distance + speed group" lines whose two parts the line joiner left apart: for every line, the nearest line to its right in
    /// the same row (centers within a quarter line height or 3 pixels), about as high, with a gap of at least 1 pixel and at most
    /// <see cref="NameplateDetectorSettings.SpeedJoinGap"/> line heights, and at most <see cref="NameplateDetectorSettings.SpeedJoinMaxLines"/> line heights
    /// wide, gives one joined box (the two boxes' union). Whether the right part starts with "[" is decided when the line is read
    /// (<see cref="DistanceLineRule"/>). Port of <c>speed_unions</c> of the Python reference.
    /// </summary>
    private List<Rect> SpeedUnions(List<Rect> boxes, int frameHeight)
    {
        var heightTolerance = Scaled(frameHeight, 4);
        var rowTolerance = Scaled(frameHeight, 3);
        var unions = new List<Rect>();
        for (var l = 0; l < boxes.Count; l++)
        {
            var left = boxes[l];
            var best = -1;
            var bestGap = 0;
            for (var r = 0; r < boxes.Count; r++)
            {
                if (r == l)
                    continue;
                var right = boxes[r];
                var gap = right.X - (left.X + left.Width);
                if (!(gap >= 1 && gap <= _settings.SpeedJoinGap * left.Height) || right.Width > _settings.SpeedJoinMaxLines * left.Height)
                    continue;
                if (Math.Abs(right.Height - left.Height) > Math.Max(heightTolerance, left.Height * 0.45))
                    continue;
                if (Math.Abs(right.Y + right.Height / 2.0 - (left.Y + left.Height / 2.0)) > Math.Max(rowTolerance, 0.25 * left.Height))
                    continue;
                if (best < 0 || gap < bestGap)
                {
                    best = r;
                    bestGap = gap;
                }
            }

            if (best >= 0)
            {
                var right = boxes[best];
                var top = Math.Min(left.Y, right.Y);
                var bottom = Math.Max(left.Y + left.Height, right.Y + right.Height);
                unions.Add(new Rect(left.X, top, right.X + right.Width - left.X, bottom - top));
            }
        }

        return unions;
    }

    // ------------------------------------------------------------------------------------------------ stage 3: pairs

    /// <summary>
    /// A name line above a distance line: centered on each other, the same height, the gap up to 1.4 line heights, and not a left aligned
    /// label column (equal left edges while the widths differ). Every such pair is returned, whether or not its lines are isolated.
    /// </summary>
    private List<(int Top, int Bottom)> PairLines(List<Rect> boxes, int frameHeight, int realCount)
    {
        var heightTolerance = Scaled(frameHeight, 4);
        var centerTolerance = Scaled(frameHeight, _settings.CenterTolerance);
        var leftTolerance = Scaled(frameHeight, _settings.LeftTolerance);
        var leftWidthDifference = Scaled(frameHeight, _settings.LeftWidthDifference);
        var pairs = new List<(int, int)>();
        for (var t = 0; t < realCount; t++) // the joined distance + speed boxes after realCount are never the upper line
        {
            var top = boxes[t];
            var topCenter = top.X + top.Width / 2.0;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (b == t)
                    continue;
                var bottom = boxes[b];
                var gap = bottom.Y - (top.Y + top.Height);
                if (!(gap >= 1 && gap <= top.Height * 1.4))
                    continue;
                if (Math.Abs(bottom.Height - top.Height) > Math.Max(heightTolerance, top.Height * 0.45))
                    continue;
                var bottomCenter = bottom.X + bottom.Width / 2.0;
                if (Math.Abs(bottomCenter - topCenter) > centerTolerance)
                    continue;
                if (Math.Abs(top.X - bottom.X) <= leftTolerance && Math.Abs(top.Width - bottom.Width) >= leftWidthDifference)
                    continue;
                pairs.Add((t, b));
            }
        }

        return pairs;
    }

    /// <summary>
    /// The rows stacked above the name line of every pair (<c>row_candidates</c> and <c>stack_rows</c> of the Python reference), keyed by the
    /// (name line, line below) boxes in mask pixels. Only pairs with more than one row, or with a blocker, are in the result.
    /// </summary>
    private Dictionary<(Rect Top, Rect Bottom), (List<Rect> Rows, List<Rect> Blockers)> FindStacks(
        List<Rect> boxes, List<(int Top, int Bottom)> pairs, int realCount, int frameHeight)
    {
        var stacks = new Dictionary<(Rect, Rect), (List<Rect>, List<Rect>)>();
        if (pairs.Count == 0)
            return stacks;
        var pool = RowCandidates(boxes, realCount, frameHeight);
        foreach (var (t, b) in pairs)
        {
            var (rows, blockers) = StackRows(pool, boxes[t], boxes[b], frameHeight);
            if (rows.Count > 1 || blockers.Count > 0)
                stacks[(boxes[t], boxes[b])] = (rows, blockers);
        }

        return stacks;
    }

    /// <summary>
    /// The lines that can be a row of a stacked name: the real boxes, except that the pieces of ONE line that the line joiner left apart (a letter next to an
    /// underscore on the line below it can break the 8-connectivity) are replaced by their union. Pieces: the same row (top within 2 px, height within 3 px), x ranges
    /// overlapping or at most 4 px apart (all at 1440 high). Used for the rows above the name line only; the name line itself and the anchor are always real boxes.
    /// </summary>
    private List<Rect> RowCandidates(List<Rect> boxes, int realCount, int frameHeight)
    {
        var s2 = Scaled(frameHeight, 2);
        var s3 = Scaled(frameHeight, 3);
        var s4 = Scaled(frameHeight, 4);
        var parent = new int[realCount];
        for (var i = 0; i < realCount; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (var i = 0; i < realCount; i++)
        {
            var a = boxes[i];
            for (var j = i + 1; j < realCount; j++)
            {
                var c = boxes[j];
                if (Math.Abs(a.Y - c.Y) <= s2 && Math.Abs(a.Height - c.Height) <= s3 && c.X <= a.X + a.Width + s4 && a.X <= c.X + c.Width + s4)
                    parent[Find(j)] = Find(i);
            }
        }

        var order = new List<int>(); // the groups in the order of their first member
        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < realCount; i++)
        {
            var root = Find(i);
            if (!members.TryGetValue(root, out var group))
            {
                members[root] = group = new List<int>();
                order.Add(root);
            }

            group.Add(i);
        }

        var pool = new List<Rect>(order.Count);
        foreach (var root in order)
        {
            var group = members[root];
            if (group.Count == 1)
            {
                pool.Add(boxes[group[0]]);
                continue;
            }

            var x0 = group.Min(i => boxes[i].X);
            var y0 = group.Min(i => boxes[i].Y);
            var x1 = group.Max(i => boxes[i].X + boxes[i].Width);
            var y1 = group.Max(i => boxes[i].Y + boxes[i].Height);
            pool.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
        }

        return pool;
    }

    /// <summary>
    /// The rows stacked above the name line <paramref name="top"/> of the pair (<paramref name="top"/>, <paramref name="anchor"/>): the rows top to bottom with
    /// <paramref name="top"/> last, and the blockers. See the class remarks for the rules; this is <c>stack_rows</c> of the Python reference. A left aligned stacked row
    /// is a HUD label column: it neither joins nor blocks.
    /// </summary>
    private (List<Rect> Rows, List<Rect> Blockers) StackRows(List<Rect> pool, Rect top, Rect anchor, int frameHeight)
    {
        var heightTolerance = Scaled(frameHeight, 4);
        var centerTolerance = Scaled(frameHeight, _settings.CenterTolerance);
        var leftTolerance = Scaled(frameHeight, _settings.LeftTolerance);
        var leftWidthDifference = Scaled(frameHeight, _settings.LeftWidthDifference);
        var anchorCenter = anchor.X + anchor.Width / 2.0;
        var chain = new List<Rect> { top };
        var current = top;
        while (true)
        {
            var joinable = new List<(int Gap, Rect Row)>();
            var broken = new List<Rect>();
            foreach (var row in pool)
            {
                if (row == anchor || chain.Contains(row))
                    continue;
                var gap = current.Y - (row.Y + row.Height);
                if (!(gap >= 1 && gap <= _settings.RowGapMax * current.Height))
                    continue;
                var pitch = current.Y - row.Y;
                if (!(_settings.RowPitchMin * current.Height <= pitch && pitch <= _settings.RowPitchMax * current.Height))
                    continue;
                if (Math.Abs(row.Height - current.Height) > Math.Max(heightTolerance, current.Height * 0.45))
                    continue;
                if (row.Width < _settings.RowMinCaps * row.Height)
                    continue; // a short line above is no wrapped row (the rows above the last one are full width)
                if (row.X >= current.X + current.Width || row.X + row.Width <= current.X)
                    continue; // no horizontal overlap: beside, not above
                if (Math.Abs(row.X - current.X) <= leftTolerance && Math.Abs(row.Width - current.Width) >= leftWidthDifference)
                    continue; // a left aligned label column
                if (Math.Abs(row.X + row.Width / 2.0 - anchorCenter) > centerTolerance)
                {
                    broken.Add(row);
                    continue;
                }

                joinable.Add((gap, row));
            }

            if (joinable.Count == 0)
            {
                chain.Reverse();
                return (chain, broken);
            }

            // nearest first; equal gaps: the smaller box (x, y, width, height), as Python's min over (gap, box) tuples
            var best = joinable[0];
            foreach (var candidate in joinable)
                if (CompareJoinable(candidate, best) < 0)
                    best = candidate;
            if (chain.Count >= _settings.MaxRows)
            {
                chain.Reverse();
                broken.Add(best.Row);
                return (chain, broken);
            }

            chain.Add(best.Row);
            current = best.Row;
        }
    }

    private static int CompareJoinable((int Gap, Rect Row) a, (int Gap, Rect Row) b)
    {
        var byGap = a.Gap.CompareTo(b.Gap);
        if (byGap != 0)
            return byGap;
        var byX = a.Row.X.CompareTo(b.Row.X);
        if (byX != 0)
            return byX;
        var byY = a.Row.Y.CompareTo(b.Row.Y);
        if (byY != 0)
            return byY;
        var byWidth = a.Row.Width.CompareTo(b.Row.Width);
        return byWidth != 0 ? byWidth : a.Row.Height.CompareTo(b.Row.Height);
    }

    /// <summary>
    /// Sorts the geometric pairs. STRICT: both lines isolated. CROWD (when <see cref="NameplateDetectorSettings.CrowdException"/> is on): the lines are
    /// isolated except for ink that lies inside the boxes of other text lines, and every one of those lines belongs to another geometric pair
    /// (a line over a line): such a pair can be a nameplate in a crowd, valid once the neighbours read as nameplates themselves
    /// (<see cref="FrameEvaluator"/>). LOOSE: everything else.
    /// </summary>
    private (List<(int Top, int Bottom)> Strict, List<(int Top, int Bottom)> Loose, List<CrowdPair> Crowd) SortPairs(
        List<Rect> boxes, bool[] clear, List<(int Top, int Bottom)> all, int realCount)
    {
        var strict = new List<(int, int)>();
        var loose = new List<(int, int)>();
        var crowd = new List<CrowdPair>();
        var pairsOfLine = new Dictionary<int, List<(int Top, int Bottom)>>();
        foreach (var pair in all)
        {
            foreach (var line in new[] { pair.Top, pair.Bottom })
            {
                if (!pairsOfLine.TryGetValue(line, out var list))
                    pairsOfLine[line] = list = new List<(int, int)>();
                list.Add(pair);
            }
        }

        var neighbourCache = new Dictionary<int, List<int>?>();
        foreach (var (t, b) in all)
        {
            if (clear[t] && clear[b])
            {
                strict.Add((t, b));
                continue;
            }

            if (_settings.CrowdException)
            {
                var need = new HashSet<int>();
                var blocked = false;
                foreach (var line in new[] { t, b })
                {
                    if (!neighbourCache.TryGetValue(line, out var near))
                        neighbourCache[line] = near = clear[line] ? new List<int>() : Neighbours(boxes, line, realCount);
                    if (near is null)
                    {
                        blocked = true;
                        break;
                    }

                    need.UnionWith(near);
                }

                if (!blocked)
                {
                    need.Remove(t);
                    need.Remove(b);
                    if (need.Count > 0 && need.All(k => pairsOfLine.TryGetValue(k, out var others) && others.Any(o => o.Top != t && o.Top != b && o.Bottom != t && o.Bottom != b)))
                    {
                        crowd.Add(new CrowdPair(t, b, need.OrderBy(k => k).ToList()));
                        continue;
                    }
                }
            }

            loose.Add((t, b));
        }

        return (strict, loose, crowd);
    }

    /// <summary>
    /// The text lines whose boxes hold the obstacle ink within <see cref="NameplateDetectorSettings.ClearLines"/> line heights to the left and right of
    /// line <paramref name="index"/> (in the rows of the line), sorted; empty when there is no such ink; null when some of that ink lies outside every
    /// other line's box (a ship, a HUD arc, a panel: the line is not isolated for good).
    /// </summary>
    private List<int>? Neighbours(List<Rect> boxes, int index, int realCount)
    {
        var box = boxes[index];
        var reach = (int)Math.Ceiling(_settings.ClearLines * box.Height);
        var rowStart = Math.Max(0, box.Y);
        var rowEnd = Math.Min(_obstacle.Rows, box.Y + box.Height);
        var found = new SortedSet<int>();
        if (rowEnd <= rowStart)
            return new List<int>();

        var sides = new[]
        {
            (Start: Math.Max(0, box.X - reach), End: box.X),
            (Start: box.X + box.Width, End: Math.Min(_obstacle.Cols, box.X + box.Width + reach)),
        };
        foreach (var (start, end) in sides)
        {
            if (end <= start)
                continue;
            using var window = new Mat(_obstacle, new Rect(start, rowStart, end - start, rowEnd - rowStart));
            if (Cv2.CountNonZero(window) == 0)
                continue;
            using var rest = window.Clone();
            for (var k = 0; k < realCount; k++) // joined distance + speed boxes (after realCount) are not neighbours
            {
                if (k == index)
                    continue;
                var other = boxes[k];
                if (other.X >= end || other.X + other.Width <= start || other.Y >= rowEnd || other.Y + other.Height <= rowStart)
                    continue;
                var x0 = Math.Max(start, other.X);
                var x1 = Math.Min(end, other.X + other.Width);
                var y0 = Math.Max(rowStart, other.Y);
                var y1 = Math.Min(rowEnd, other.Y + other.Height);
                var overlap = new Rect(x0 - start, y0 - rowStart, x1 - x0, y1 - y0);
                using (var part = new Mat(window, overlap))
                    if (Cv2.CountNonZero(part) > 0)
                        found.Add(k);
                using (var restPart = new Mat(rest, overlap))
                    restPart.SetTo(Scalar.All(0));
            }

            if (Cv2.CountNonZero(rest) > 0)
                return null;
        }

        return found.ToList();
    }

    // ------------------------------------------------------------------------------------------------ stage 4: reading

    /// <summary>One word: no gap between two pieces wider than a word gap (in capital heights).</summary>
    private bool IsOneWord(PieceRead read)
    {
        var pieces = read.Pieces;
        for (var i = 0; i + 1 < pieces.Count; i++)
            if (pieces[i + 1].Start - pieces[i].End > _settings.WordGap * read.CapHeight)
                return false;
        return true;
    }
}
