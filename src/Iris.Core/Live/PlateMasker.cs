using Iris.Core.Detection;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>The colour class of a nameplate's text: the detector's colour rules, one by one.</summary>
public enum PlateColorClass
{
    /// <summary>Any text the detector's mask accepts (cyan/white, red and green together): used when no single class is known.</summary>
    Any,

    /// <summary>Cyan or white text (neutral and targeted plates).</summary>
    CyanWhite,

    /// <summary>Red text (hostile).</summary>
    Red,

    /// <summary>Green text (friendly).</summary>
    Green,
}

/// <summary>
/// The text mask of the tracking, made the way the detector makes its own: a pixel is text when it is locally brighter than its surroundings (top-hat of its
/// channel, the window 11 px at 1440 high, scaled with the game window's height) AND has the colour of the class: cyan/white, red (hostile) or green (friendly).
/// The thresholds are those of <c>NameplateDetector.BuildColorMask</c> (private there, in step with it by hand: 28 for cyan/white and red, 20 for green).
/// The result is 8 bit, one channel, 255 for text and 0 for not. The template of a plate and the search patches are made with the same class, so they compare like with like.
/// Not thread safe (it reuses its buffers): one per thread.
/// </summary>
public sealed class PlateMasker : IDisposable
{
    private const double TopHatMin = 28;
    private const double GreenTopHatMin = 20;

    private readonly NameplateDetectorSettings _settings;
    private readonly Dictionary<int, Mat> _kernels = new();
    private readonly Mat _blue = new();
    private readonly Mat _green = new();
    private readonly Mat _red = new();
    private readonly Mat _hat = new();
    private readonly Mat _scratch = new();
    private readonly Mat _cyanWhite = new();
    private readonly Mat _redMask = new();
    private readonly Mat _greenMask = new();
    private bool _disposed;

    public PlateMasker(NameplateDetectorSettings? settings = null) => _settings = settings ?? NameplateDetectorSettings.Default;

    /// <summary>The top-hat window (odd) for a game window <paramref name="scaleHeight"/> high: 11 px at 1440, as the detector's.</summary>
    public int KernelSize(int scaleHeight)
    {
        var k = _settings.Scale(scaleHeight, 11);
        return k % 2 == 1 ? k : k + 1;
    }

    /// <summary>The mask of <paramref name="cls"/> for the BGR picture <paramref name="bgr"/> into <paramref name="dst"/> (same size, 8 bit, one channel).</summary>
    public void Build(Mat bgr, int scaleHeight, PlateColorClass cls, Mat dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Split(bgr);
        var kernel = Kernel(KernelSize(scaleHeight));
        switch (cls)
        {
            case PlateColorClass.CyanWhite:
                BuildCyanWhite(kernel);
                _cyanWhite.CopyTo(dst);
                break;
            case PlateColorClass.Red:
                BuildRed(kernel);
                _redMask.CopyTo(dst);
                break;
            case PlateColorClass.Green:
                BuildGreen(kernel);
                _greenMask.CopyTo(dst);
                break;
            default:
                BuildCyanWhite(kernel);
                BuildRed(kernel);
                BuildGreen(kernel);
                Cv2.BitwiseOr(_cyanWhite, _redMask, dst);
                Cv2.BitwiseOr(dst, _greenMask, dst);
                break;
        }
    }

    /// <summary>
    /// The class of the text inside <paramref name="box"/> of <paramref name="bgr"/>: the class with the most text pixels there, at least
    /// <paramref name="minPixels"/> of them; <see cref="PlateColorClass.Any"/> when no class has that much (or when it is a tie between classes).
    /// </summary>
    public PlateColorClass Classify(Mat bgr, int scaleHeight, Rect box, int minPixels = 8)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Split(bgr);
        var kernel = Kernel(KernelSize(scaleHeight));
        BuildCyanWhite(kernel);
        BuildRed(kernel);
        BuildGreen(kernel);

        var bounds = new Rect(0, 0, bgr.Width, bgr.Height);
        var inside = Intersect(box, bounds);
        if (inside.Width <= 0 || inside.Height <= 0)
            return PlateColorClass.Any;

        int Count(Mat mask)
        {
            using var part = new Mat(mask, inside);
            return Cv2.CountNonZero(part);
        }

        var cyan = Count(_cyanWhite);
        var red = Count(_redMask);
        var green = Count(_greenMask);
        var best = Math.Max(cyan, Math.Max(red, green));
        if (best < minPixels)
            return PlateColorClass.Any;
        var winners = (cyan == best ? 1 : 0) + (red == best ? 1 : 0) + (green == best ? 1 : 0);
        if (winners > 1)
            return PlateColorClass.Any;
        return cyan == best ? PlateColorClass.CyanWhite : red == best ? PlateColorClass.Red : PlateColorClass.Green;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mat in new[] { _blue, _green, _red, _hat, _scratch, _cyanWhite, _redMask, _greenMask })
            mat.Dispose();
        foreach (var kernel in _kernels.Values)
            kernel.Dispose();
        _kernels.Clear();
    }

    internal static Rect Intersect(Rect a, Rect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right <= left || bottom <= top ? new Rect() : new Rect(left, top, right - left, bottom - top);
    }

    private void Split(Mat bgr)
    {
        if (bgr.Empty() || bgr.Depth() != MatType.CV_8U || bgr.Channels() != 3)
            throw new ArgumentException("Expected a non-empty 8 bit BGR image.", nameof(bgr));
        Cv2.ExtractChannel(bgr, _blue, 0);
        Cv2.ExtractChannel(bgr, _green, 1);
        Cv2.ExtractChannel(bgr, _red, 2);
    }

    private Mat Kernel(int size)
    {
        if (!_kernels.TryGetValue(size, out var kernel))
        {
            kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(size, size));
            _kernels[size] = kernel;
        }

        return kernel;
    }

    // The rules below are the detector's (NameplateDetector.BuildColorMask), operation for operation.

    // cyan / white: th_b > 28 and b > 150 and g > 140 and b >= r
    private void BuildCyanWhite(Mat kernel)
    {
        Cv2.MorphologyEx(_blue, _hat, MorphTypes.TopHat, kernel);
        Cv2.Threshold(_hat, _cyanWhite, TopHatMin, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_blue, _scratch, 150, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
        Cv2.Threshold(_green, _scratch, 140, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
        Cv2.Subtract(_red, _blue, _scratch); // 0 exactly when r <= b
        Cv2.Threshold(_scratch, _scratch, 0, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(_cyanWhite, _scratch, _cyanWhite);
    }

    // red: th_r > 28 and r > 140 and r - g > 80 and r - b > 60
    private void BuildRed(Mat kernel)
    {
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
    }

    // friendly green: th_g > 20 and g > 90 and g - r > 35 and g - b > 30
    private void BuildGreen(Mat kernel)
    {
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
    }
}
