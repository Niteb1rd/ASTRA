using System.Runtime.InteropServices;
using Iris.Core.Detection.Letters;
using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>
/// Finds nameplates in a game frame and reads their names, with the trained letter model only (no Tesseract).
/// <list type="number">
/// <item>Crop to the scan region (<see cref="NameplateDetectorSettings"/>).</item>
/// <item>Color mask: a pixel is text when it is locally brighter than its 11x11 surroundings (top-hat of its channel) AND cyan or
/// white, red (hostile) or green (friendly) in hue. Local contrast works on dark space and on bright sky alike.</item>
/// <item>Drop shapes taller than a letter (reticle arcs, frames), join the letters of a word with a 7x1 dilation, keep the
/// connected shapes that look like a text line (10 to 22 pixels high, 24 to 400 wide, partly filled).</item>
/// <item>Pair lines: a name line with a line directly below it (gap up to 1.4 line heights), about as high, centered on it.</item>
/// <item>Read the name with the letter model; it must look like a handle (4 to 25 characters) and be sure enough.</item>
/// <item>Read the line below with the same model restricted to the distance alphabet; <see cref="DistanceLineRule"/> decides
/// whether it is a distance. Then the pair is a nameplate.</item>
/// <item>Stacked nameplates: a name directly above a confirmed nameplate hides its own distance line; it is accepted too.</item>
/// </list>
/// All pixel constants are for 1440 pixel high frames and scale with the real frame height (see
/// <see cref="NameplateDetectorSettings.ReferenceHeight"/>). The detector reuses its image buffers between frames, so one instance
/// handles one frame at a time (it is not thread safe); give each thread its own, they can share the <see cref="NameplateReader"/>
/// and its model. Dispose it when done.
/// <para>
/// One deliberate difference from the Python reference: a frame with very many candidate lines reads at most
/// <see cref="NameplateDetectorSettings.MaxCandidateTopsPerFrame"/> of them (a work budget against noisy frames; the default is far
/// above what real frames and the parity fixtures need).
/// </para>
/// </summary>
public sealed class NameplateDetector : IDisposable
{
    // Top-hat thresholds and color rules of the text mask (0 to 255 intensities, not pixel sizes: not scaled).
    private const double TopHatMin = 28;
    private const double GreenTopHatMin = 20;

    private readonly NameplateReader _reader;
    private readonly NameplateDetectorSettings _settings;
    private readonly bool[] _distanceAlphabet;
    private readonly DistanceLineRule _distanceRule;
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
        _distanceRule = new DistanceLineRule(_settings);
    }

    /// <summary>
    /// Finds the nameplates in a BGR, 8 bit, 3 channel frame. The frame is only read. An empty list when there are none.
    /// </summary>
    public IReadOnlyList<NameplateDetection> Detect(Mat bgr)
    {
        var candidates = FindCandidates(bgr);
        var boxes = candidates.Boxes;
        var pairs = candidates.Pairs;
        var found = new List<NameplateDetection>();
        if (pairs.Count == 0)
            return found;

        // a box can be in several pairs: read each of them once
        var names = new (string Text, double Score)?[boxes.Count];
        var distances = new PieceRead?[boxes.Count];
        var distanceRead = new bool[boxes.Count];
        var confirmed = new HashSet<int>();
        var namesRead = 0;

        // Work budget (a deliberate difference from the Python reference, see NameplateDetectorSettings.MaxCandidateTopsPerFrame):
        // at most that many different name lines are read per frame; once it is used up, pairs whose upper line is new are skipped.
        bool CanRead(int i) => names[i] is not null || namesRead < _settings.MaxCandidateTopsPerFrame;

        (string Text, double Score) Name(int i)
        {
            if (names[i] is null)
            {
                names[i] = ReadName(bgr, boxes[i]);
                namesRead++;
            }

            return names[i]!.Value;
        }

        foreach (var (top, bottom) in pairs)
        {
            if (confirmed.Contains(top) || !CanRead(top))
                continue;
            var (text, score) = Name(top);
            if (!IsName(text, score))
                continue; // cheap reject first: the line above must read like a handle

            if (!distanceRead[bottom])
            {
                distances[bottom] = _reader.ReadPieces(bgr, boxes[bottom], _distanceAlphabet);
                distanceRead[bottom] = true;
            }

            var read = distances[bottom];
            var verdict = _distanceRule.Evaluate(read);
            var accepted = verdict == DistanceVerdict.Full
                || (verdict == DistanceVerdict.Partial
                    && score / text.Length >= _settings.PartialNameScore
                    && text.Length >= _settings.PartialMinNameLength);
            if (!accepted)
                continue;

            found.Add(new NameplateDetection(text, score, boxes[top], boxes[bottom], string.Concat(read!.Pieces.Select(p => p.Text)), false));
            confirmed.Add(top);
        }

        // stacked nameplates: a name directly above a confirmed name hides its own distance line
        foreach (var (top, bottom) in pairs)
        {
            if (confirmed.Contains(top) || !confirmed.Contains(bottom) || !CanRead(top))
                continue;
            var (text, score) = Name(top);
            if (!IsName(text, score))
                continue;
            found.Add(new NameplateDetection(text, score, boxes[top], boxes[bottom], string.Empty, true));
            confirmed.Add(top);
        }

        return found;
    }

    /// <summary>
    /// Stages 1 to 4 only: the text lines of the frame and the pairs that could be nameplates. No reading. Public so that tests and
    /// tools can look at the geometry on its own.
    /// </summary>
    public NameplateCandidates FindCandidates(Mat bgr)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));

        var width = bgr.Width;
        var height = bgr.Height;
        // same arithmetic as the Python reference: int(fraction * size), truncating
        var x0 = (int)(_settings.RegionLeft * width);
        var x1 = Math.Min(width, (int)(_settings.RegionRight * width));
        var y0 = (int)(_settings.RegionTop * height);
        var y1 = Math.Min(height, (int)(_settings.RegionBottom * height));
        var empty = new NameplateCandidates(Array.Empty<Rect>(), Array.Empty<(int, int)>());
        if (x1 <= x0 || y1 <= y0)
            return empty;

        using var region = new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
        BuildColorMask(region, ScaledOdd(height, 11));
        var local = FindTextLines(height);
        if (local.Count == 0)
            return empty;

        var pairs = PairLines(local, height);
        var boxes = new Rect[local.Count];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new Rect(local[i].X + x0, local[i].Y + y0, local[i].Width, local[i].Height);
        return new NameplateCandidates(boxes, pairs);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mat in new[] { _blue, _green, _red, _hat, _cyanWhite, _redMask, _greenMask, _scratch, _mask, _kept, _labelHit, _joined, _labels, _stats, _centroids })
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

    // ------------------------------------------------------------------------------------------------ stage 2: color mask

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

    // ------------------------------------------------------------------------------------------------ stage 3: text lines

    /// <summary>
    /// Boxes (in region pixels) of the text lines of <see cref="_mask"/>: shapes taller than a letter are removed, the letters
    /// of a word are joined by a wide, flat dilation, and a joined shape is a line when its size is right and it is partly,
    /// but not mostly, filled (text, not a solid bar or a hairline).
    /// </summary>
    private List<Rect> FindTextLines(int frameHeight)
    {
        var maxHeight = Scaled(frameHeight, 22);
        var minHeight = Scaled(frameHeight, 10);
        var minWidth = Scaled(frameHeight, 24);
        var maxWidth = Scaled(frameHeight, 400);

        var kept = DropTall(maxHeight);
        var boxes = new List<Rect>();
        if (kept is null)
            return boxes;

        Cv2.Dilate(kept, _joined, Kernel(Scaled(frameHeight, 7), 1));
        var count = Cv2.ConnectedComponentsWithStats(_joined, _labels, _stats, _centroids, PixelConnectivity.Connectivity8);
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

    /// <summary>
    /// The mask without the connected shapes taller than <paramref name="maxHeight"/>. Returns <see cref="_mask"/> itself when
    /// nothing is that tall, <see cref="_kept"/> otherwise, null when the mask is empty.
    /// </summary>
    private Mat? DropTall(int maxHeight)
    {
        var count = Cv2.ConnectedComponentsWithStats(_mask, _labels, _stats, _centroids, PixelConnectivity.Connectivity8);
        if (count <= 1)
            return null;
        if (_labels.Type() != MatType.CV_32SC1)
            throw new InvalidOperationException($"Unexpected label image type {_labels.Type()}.");
        CopyStats(count);

        var anyTall = false;
        for (var i = 1; i < count; i++)
            if (_statsBuffer[i * 5 + 3] > maxHeight)
            {
                anyTall = true;
                break;
            }

        if (!anyTall)
            return _mask;

        // Same result as keeping a pixel when its label is not 0 and its component is not too tall, without a loop over the
        // pixels in managed code: copy the mask, then clear every tall component inside its bounding box (the label image says
        // which pixels of the box belong to it; other shapes inside the same box stay).
        _mask.CopyTo(_kept);
        for (var i = 1; i < count; i++)
        {
            if (_statsBuffer[i * 5 + 3] <= maxHeight)
                continue;
            var box = new Rect(_statsBuffer[i * 5 + 0], _statsBuffer[i * 5 + 1], _statsBuffer[i * 5 + 2], _statsBuffer[i * 5 + 3]);
            using var labelsRoi = new Mat(_labels, box);
            using var keptRoi = new Mat(_kept, box);
            Cv2.Compare(labelsRoi, new Scalar(i), _labelHit, CmpType.EQ);
            keptRoi.SetTo(Scalar.All(0), _labelHit);
        }

        return _kept;
    }

    private void CopyStats(int count)
    {
        var values = count * 5;
        if (_statsBuffer.Length < values)
            _statsBuffer = new int[values];
        Marshal.Copy(_stats.Data, _statsBuffer, 0, values);
    }

    // ------------------------------------------------------------------------------------------------ stage 4: pairs

    /// <summary>
    /// A name line above a distance line, horizontally centered on each other (the "[speed]" suffix extends the distance line
    /// to the right, so the centers may differ by half the wider line plus a few pixels).
    /// </summary>
    private List<(int Top, int Bottom)> PairLines(List<Rect> boxes, int frameHeight)
    {
        var tolerance = Scaled(frameHeight, 4);
        var centerSlack = Scaled(frameHeight, 6);
        var pairs = new List<(int, int)>();
        for (var t = 0; t < boxes.Count; t++)
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
                if (Math.Abs(bottom.Height - top.Height) > Math.Max(tolerance, top.Height * 0.45))
                    continue;
                var bottomCenter = bottom.X + bottom.Width / 2.0;
                if (Math.Abs(bottomCenter - topCenter) > Math.Max(top.Width, bottom.Width) * 0.5 + centerSlack)
                    continue;
                pairs.Add((t, b));
            }
        }

        return pairs;
    }

    // ------------------------------------------------------------------------------------------------ stage 5: reading

    private (string Text, double Score) ReadName(Mat bgr, Rect box)
    {
        var read = _reader.ReadPieces(bgr, box);
        if (read is null || read.Pieces.Count == 0)
            return (string.Empty, 0.0);
        var score = 0.0;
        foreach (var piece in read.Pieces)
            score += piece.Score;
        return (string.Concat(read.Pieces.Select(p => p.Text)), score);
    }

    /// <summary>A handle: 4 to 25 characters of A-Z, 0-9, "-" and "_", read surely enough.</summary>
    private bool IsName(string text, double score)
    {
        if (text.Length < _settings.MinNameLength || text.Length > _settings.MaxNameLength)
            return false;
        foreach (var ch in text)
            if (!(ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        return score / text.Length >= _settings.MinNameScore;
    }
}
