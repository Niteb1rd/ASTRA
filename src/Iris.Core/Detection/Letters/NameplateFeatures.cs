using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Iris.Core.Detection.Letters;

/// <summary>Top and bottom row (inclusive) of the capital letters of a line.</summary>
public readonly record struct CapBand(int Top, int Bottom)
{
    /// <summary>Height of a capital letter in mask pixels.</summary>
    public int Height => Bottom - Top + 1;
}

/// <summary>
/// The image side of the letter recognizer: nameplate crop to text mask, capital band, cuts at blank columns and the 388-number
/// description of one letter. Every function reproduces the Python prototype (<c>fontrec.py</c>) step by step, including its
/// rounding and comparison rules, so that the C# and Python results agree. The names in parentheses are the Python functions.
/// </summary>
public static class NameplateFeatures
{
    /// <summary>Enlargement of the crop before the mask is made.</summary>
    public const int Scale = 4;

    /// <summary>Size of the letter picture inside the vector.</summary>
    public const int GlyphWidth = 16, GlyphHeight = 24;

    /// <summary>16 * 24 picture values + 4 size and position values.</summary>
    public const int VectorLength = GlyphWidth * GlyphHeight + 4;

    /// <summary>
    /// Text mask of the nameplate line inside <paramref name="box"/> of a BGR (8 bit, 3 channel) image (<c>line_mask</c>).
    /// Returns null when the crop has no text. <paramref name="threshold"/> null picks the contrast threshold per crop
    /// (Otsu's method, limited to 25..90).
    /// </summary>
    public static LineMask? CreateMask(Mat bgr, Rect box, double? threshold = null, int pad = 2)
    {
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));

        var x0 = Math.Max(0, box.X - pad);
        var y0 = Math.Max(0, box.Y - pad);
        var x1 = Math.Min(bgr.Width, box.X + box.Width + pad);
        var y1 = Math.Min(bgr.Height, box.Y + box.Height + pad);
        if (x1 <= x0 || y1 <= y0)
            return null;

        using var crop = new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0));
        return CreateMaskFromCrop(crop, threshold);
    }

    /// <summary>
    /// Same as <see cref="CreateMask(Mat, Rect, double?, int)"/> for a pixel buffer: 3 bytes per pixel (B, G, R), rows
    /// <paramref name="stride"/> bytes apart. Only the box (plus padding) is copied.
    /// </summary>
    public static LineMask? CreateMask(byte[] bgr, int width, int height, int stride, Rect box, double? threshold = null, int pad = 2)
    {
        if (width <= 0 || height <= 0 || stride < width * 3 || bgr.Length < (height - 1) * stride + width * 3)
            throw new ArgumentException("The buffer is smaller than width, height and stride say.", nameof(bgr));

        var x0 = Math.Max(0, box.X - pad);
        var y0 = Math.Max(0, box.Y - pad);
        var x1 = Math.Min(width, box.X + box.Width + pad);
        var y1 = Math.Min(height, box.Y + box.Height + pad);
        if (x1 <= x0 || y1 <= y0)
            return null;

        var w = x1 - x0;
        var h = y1 - y0;
        using var crop = new Mat(h, w, MatType.CV_8UC3); // freshly allocated Mats have no row padding
        for (var y = 0; y < h; y++)
            Marshal.Copy(bgr, (y0 + y) * stride + x0 * 3, crop.Data + y * w * 3, w * 3);
        return CreateMaskFromCrop(crop, threshold);
    }

    private static LineMask? CreateMaskFromCrop(Mat crop, double? threshold)
    {
        using var big = new Mat();
        Cv2.Resize(crop, big, new Size(0, 0), Scale, Scale, InterpolationFlags.Cubic);

        // brightest local contrast over the three channels: max of the top-hats (31x31 rectangle)
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(31, 31));
        var channels = Cv2.Split(big);
        using var topHat = new Mat();
        try
        {
            using var one = new Mat();
            for (var i = 0; i < channels.Length; i++)
            {
                if (i == 0)
                {
                    Cv2.MorphologyEx(channels[i], topHat, MorphTypes.TopHat, kernel);
                }
                else
                {
                    Cv2.MorphologyEx(channels[i], one, MorphTypes.TopHat, kernel);
                    Cv2.Max(topHat, one, topHat);
                }
            }
        }
        finally
        {
            foreach (var c in channels)
                c.Dispose();
        }

        double thr;
        if (threshold.HasValue)
        {
            thr = threshold.Value;
        }
        else
        {
            using var scratch = new Mat();
            var otsu = Cv2.Threshold(topHat, scratch, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            thr = Math.Clamp(otsu, 25.0, 90.0);
        }

        using var binary = new Mat();
        Cv2.Threshold(topHat, binary, thr, 1, ThresholdTypes.Binary); // 1 where top-hat > thr (strictly)

        // break 1-pixel bridges between touching letters: opening with a 3 rows x 1 column kernel
        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3));
        using var opened = new Mat();
        Cv2.MorphologyEx(binary, opened, MorphTypes.Open, openKernel);

        var w = opened.Width;
        var h = opened.Height;
        var data = new byte[w * h];
        if (opened.IsContinuous())
        {
            Marshal.Copy(opened.Data, data, 0, data.Length);
        }
        else
        {
            using var copy = opened.Clone();
            Marshal.Copy(copy.Data, data, 0, data.Length);
        }

        return TrimToText(data, h, w);
    }

    /// <summary>Keeps the rows with ink (more than 2% of the width), then the columns with ink (second half of <c>line_mask</c>).</summary>
    internal static LineMask? TrimToText(byte[] data, int h, int w)
    {
        var rowLimit = 0.02 * w;
        int firstRow = -1, lastRow = -1;
        for (var y = 0; y < h; y++)
        {
            var sum = 0;
            for (var x = 0; x < w; x++)
                sum += data[y * w + x];
            if (sum > rowLimit)
            {
                if (firstRow < 0)
                    firstRow = y;
                lastRow = y;
            }
        }

        if (firstRow < 0)
            return null;

        int firstCol = int.MaxValue, lastCol = -1;
        for (var y = firstRow; y <= lastRow; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (data[y * w + x] == 0)
                    continue;
                if (x < firstCol)
                    firstCol = x;
                if (x > lastCol)
                    lastCol = x;
            }
        }

        if (lastCol < 0)
            return null;

        var outH = lastRow - firstRow + 1;
        var outW = lastCol - firstCol + 1;
        var result = new byte[outH * outW];
        for (var y = 0; y < outH; y++)
            Buffer.BlockCopy(data, (firstRow + y) * w + firstCol, result, y * outW, outW);
        return new LineMask(outH, outW, result);
    }

    /// <summary>
    /// Columns without any ink separate the pieces of a line: returns [start, end) of every run of columns with ink (<c>gap_cuts</c>).
    /// </summary>
    public static List<(int Start, int End)> GapCuts(LineMask m)
    {
        var cuts = new List<(int, int)>();
        var start = -1;
        for (var x = 0; x < m.Width; x++)
        {
            var hasInk = false;
            for (var y = 0; y < m.Height; y++)
            {
                if (m.Data[y * m.Width + x] != 0)
                {
                    hasInk = true;
                    break;
                }
            }

            if (hasInk && start < 0)
            {
                start = x;
            }
            else if (!hasInk && start >= 0)
            {
                cuts.Add((start, x));
                start = -1;
            }
        }

        if (start >= 0)
            cuts.Add((start, m.Width));
        return cuts;
    }

    /// <summary>
    /// Top and bottom row of the capital letters: the median over all pieces that are taller than half the line (<c>cap_band</c>).
    /// Underscores and hyphens are lower than that, so they do not disturb it.
    /// </summary>
    public static CapBand FindCapBand(LineMask m)
    {
        var tops = new List<int>();
        var bottoms = new List<int>();
        foreach (var (a, b) in GapCuts(m))
        {
            int min = -1, max = -1;
            for (var y = 0; y < m.Height; y++)
            {
                for (var x = a; x < b; x++)
                {
                    if (m.Data[y * m.Width + x] == 0)
                        continue;
                    if (min < 0)
                        min = y;
                    max = y;
                    break;
                }
            }

            if (min >= 0 && max - min > 0.5 * m.Height)
            {
                tops.Add(min);
                bottoms.Add(max);
            }
        }

        if (tops.Count == 0)
            return new CapBand(0, m.Height - 1);
        return new CapBand(Median(tops), Median(bottoms));
    }

    /// <summary>numpy.median converted with int(): mean of the two middle values for an even count, truncated.</summary>
    private static int Median(List<int> values)
    {
        values.Sort();
        var n = values.Count;
        var median = (n & 1) == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
        return (int)median;
    }

    /// <summary>
    /// The 388 numbers that describe the letter in columns [x0, x1) (<c>glyph_vec</c>): its bounding box shrunk or stretched to
    /// 16x24 (area averaging), then its width, height and offsets from the capital band, relative to the capital height, times 4.
    /// Null when the columns hold no ink.
    /// </summary>
    public static double[]? GlyphVector(LineMask m, int x0, int x1, CapBand band)
    {
        x1 = Math.Min(x1, m.Width);
        var cap = Math.Max(1, band.Bottom - band.Top + 1);
        int rowMin = -1, rowMax = -1, colMin = int.MaxValue, colMax = -1;
        for (var y = 0; y < m.Height; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (m.Data[y * m.Width + x] == 0)
                    continue;
                if (rowMin < 0)
                    rowMin = y;
                rowMax = y;
                if (x < colMin)
                    colMin = x;
                if (x > colMax)
                    colMax = x;
            }
        }

        if (rowMin < 0)
            return null;

        var gh = rowMax - rowMin + 1;
        var gw = colMax - colMin + 1;
        var pixels = new float[gh * gw];
        for (var y = 0; y < gh; y++)
            for (var x = 0; x < gw; x++)
                pixels[y * gw + x] = m.Data[(rowMin + y) * m.Width + colMin + x];

        var vector = new double[VectorLength];
        using (var src = new Mat(gh, gw, MatType.CV_32FC1))
        using (var dst = new Mat())
        {
            Marshal.Copy(pixels, 0, src.Data, pixels.Length);
            Cv2.Resize(src, dst, new Size(GlyphWidth, GlyphHeight), 0, 0, InterpolationFlags.Area);
            var small = new float[GlyphWidth * GlyphHeight];
            if (dst.IsContinuous())
            {
                Marshal.Copy(dst.Data, small, 0, small.Length);
            }
            else
            {
                using var copy = dst.Clone();
                Marshal.Copy(copy.Data, small, 0, small.Length);
            }

            for (var i = 0; i < small.Length; i++)
                vector[i] = small[i];
        }

        var n = GlyphWidth * GlyphHeight;
        vector[n] = (double)gw / cap * 4.0;
        vector[n + 1] = (double)gh / cap * 4.0;
        vector[n + 2] = (double)(rowMin - band.Top) / cap * 4.0;
        vector[n + 3] = (double)(rowMax - band.Bottom) / cap * 4.0;
        return vector;
    }
}
