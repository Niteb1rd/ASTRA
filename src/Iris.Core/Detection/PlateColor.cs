using Iris.Core.Detection.Letters;

namespace Iris.Core.Detection;

/// <summary>
/// The colour class of a nameplate, from the colour of its text. <see cref="White"/> includes the light cyan of normal plates (and every weak tint);
/// the party members of the player appear twice, as a <see cref="Blue"/> and a <see cref="Green"/> plate, and hostile plates are <see cref="Red"/>.
/// The duplicate name rule (<see cref="SessionExclusions"/>) looks at white plates only.
/// </summary>
public enum PlateColor
{
    White,
    Blue,
    Green,
    Red,
}

/// <summary>
/// The thresholds of <see cref="PlateColors.Classify"/>: hue in degrees (0 to 360), saturation 0 to 1. The defaults are the values of <c>Params</c> in
/// <c>detector_ref.py</c> (the "Model v4" colour classes, measured on the footage: WHITE-cyan hue about 184 and saturation 0.15 to 0.5, BLUE hue 196 to 218 with
/// saturation 0.25 to 0.48, GREEN hue about 120, RED hue about 358 with saturation about 0.97).
/// </summary>
public sealed record PlateColorSettings
{
    /// <summary>A saturation below this is WHITE (pure white, no tint).</summary>
    public double WhiteSaturation { get; init; } = 0.15;

    /// <summary>Saturated enough for GREEN and RED.</summary>
    public double GreenSaturation { get; init; } = 0.45;

    /// <summary>Hue range of GREEN (with <see cref="GreenSaturation"/>): from (inclusive) to (exclusive).</summary>
    public double GreenHueFrom { get; init; } = 40.0;

    public double GreenHueTo { get; init; } = 165.0;

    /// <summary>Hue range of RED (with <see cref="GreenSaturation"/>); it wraps around 0: a hue at or above <see cref="RedHueFrom"/> or below <see cref="RedHueTo"/>.</summary>
    public double RedHueFrom { get; init; } = 330.0;

    public double RedHueTo { get; init; } = 40.0;

    /// <summary>Hue range of BLUE: from (inclusive) to (exclusive), with at least <see cref="BlueSaturation"/>.</summary>
    public double BlueHueFrom { get; init; } = 195.0;

    public double BlueHueTo { get; init; } = 270.0;

    public double BlueSaturation { get; init; } = 0.25;

    public static PlateColorSettings Default { get; } = new();
}

/// <summary>The colour of the text of a plate: hue (degrees), saturation and value (0 to 1) of the median text colour, and how many text pixels it was measured on.</summary>
public readonly record struct PlateColorStats(double Hue, double Saturation, double Value, int Pixels);

/// <summary>The class of a plate with the numbers it came from.</summary>
public readonly record struct PlateColorReading(PlateColor Color, double Hue, double Saturation);

/// <summary>
/// Classifies the colour of a nameplate exactly as <c>plate_color</c>, <c>plate_color_stats</c> and <c>classify_color</c> of <c>detector_ref.py</c> do: the text
/// pixels of the plate's name rows (the line masks, see <see cref="LineMask.InkColors"/>), the per-channel median colour of the BRIGHTER HALF of them (the pixels whose
/// brightest channel is at least the median of that value: the anti-aliased edge of a glyph is mixed with the background and would pull the colour toward it),
/// converted to hue and saturation, then the class from the thresholds of <see cref="PlateColorSettings"/>.
/// </summary>
public static class PlateColors
{
    /// <summary>Fewer text pixels than this: no colour (too little to measure).</summary>
    public const int MinPixels = 30;

    /// <summary>
    /// The colour of the text of the given line masks together (the masks must have been made with <c>keepColors</c>), or null when there are fewer than
    /// <see cref="MinPixels"/> text pixels (<c>plate_color_stats</c>).
    /// </summary>
    public static PlateColorStats? Measure(IEnumerable<LineMask> rows)
    {
        var all = new List<byte[]>();
        var total = 0;
        foreach (var row in rows)
        {
            if (row.InkColors is null)
                continue;
            all.Add(row.InkColors);
            total += row.InkColors.Length / 3;
        }

        if (total < MinPixels)
            return null;

        // brightness (the largest channel) of every pixel: the threshold is the element at index n/2 of the sorted values (numpy: np.sort(v)[len(v) // 2])
        var brightness = new int[256];
        foreach (var colors in all)
        {
            for (var i = 0; i < colors.Length; i += 3)
                brightness[Math.Max(colors[i], Math.Max(colors[i + 1], colors[i + 2]))]++;
        }

        var threshold = ValueAtSortedIndex(brightness, total / 2);

        // per channel histograms of the pixels at or above the threshold ("core")
        var blue = new int[256];
        var green = new int[256];
        var red = new int[256];
        var core = 0;
        foreach (var colors in all)
        {
            for (var i = 0; i < colors.Length; i += 3)
            {
                var b = colors[i];
                var g = colors[i + 1];
                var r = colors[i + 2];
                if (Math.Max(b, Math.Max(g, r)) < threshold)
                    continue;
                blue[b]++;
                green[g]++;
                red[r]++;
                core++;
            }
        }

        var bm = Median(blue, core);
        var gm = Median(green, core);
        var rm = Median(red, core);
        var mx = Math.Max(rm, Math.Max(gm, bm));
        var mn = Math.Min(rm, Math.Min(gm, bm));
        var saturation = mx == 0 ? 0.0 : (mx - mn) / mx;
        double hue;
        if (mx == mn)
            hue = 0.0;
        else if (mx == rm)
            hue = Mod360(60.0 * ((gm - bm) / (mx - mn)));
        else if (mx == gm)
            hue = 60.0 * ((bm - rm) / (mx - mn)) + 120.0;
        else
            hue = 60.0 * ((rm - gm) / (mx - mn)) + 240.0;
        return new PlateColorStats(hue, saturation, mx / 255.0, total);
    }

    /// <summary>
    /// The class of a plate from its name rows: the class with the hue and saturation it was decided on, or null when there are too few text pixels
    /// (<c>plate_color</c>).
    /// </summary>
    public static PlateColorReading? Classify(IEnumerable<LineMask> rows, PlateColorSettings? settings = null)
    {
        var stats = Measure(rows);
        if (stats is null)
            return null;
        return new PlateColorReading(Classify(stats.Value.Hue, stats.Value.Saturation, settings), stats.Value.Hue, stats.Value.Saturation);
    }

    /// <summary>
    /// The class for a hue (degrees, 0 to 360) and saturation (0 to 1) (<c>classify_color</c>): WHITE is everything unsaturated (below
    /// <see cref="PlateColorSettings.WhiteSaturation"/>) and the light cyan of normal plates; GREEN and RED need <see cref="PlateColorSettings.GreenSaturation"/>;
    /// BLUE needs its hue range and <see cref="PlateColorSettings.BlueSaturation"/>; anything else (a weak tint) is WHITE.
    /// </summary>
    public static PlateColor Classify(double hue, double saturation, PlateColorSettings? settings = null)
    {
        var p = settings ?? PlateColorSettings.Default;
        if (saturation < p.WhiteSaturation)
            return PlateColor.White;
        if (saturation >= p.GreenSaturation)
        {
            if (p.GreenHueFrom <= hue && hue < p.GreenHueTo)
                return PlateColor.Green;
            if (hue >= p.RedHueFrom || hue < p.RedHueTo)
                return PlateColor.Red;
        }

        if (p.BlueHueFrom <= hue && hue < p.BlueHueTo && saturation >= p.BlueSaturation)
            return PlateColor.Blue;
        return PlateColor.White;
    }

    /// <summary>The value of the element at <paramref name="index"/> (0 based) of the sorted values that the histogram stands for.</summary>
    private static int ValueAtSortedIndex(int[] histogram, int index)
    {
        var seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (index < seen)
                return value;
        }

        return histogram.Length - 1;
    }

    /// <summary>numpy.median of the values the histogram stands for: the middle one, or the mean of the two middle ones for an even count.</summary>
    private static double Median(int[] histogram, int count)
    {
        if (count % 2 == 1)
            return ValueAtSortedIndex(histogram, count / 2);
        return (ValueAtSortedIndex(histogram, count / 2 - 1) + ValueAtSortedIndex(histogram, count / 2)) / 2.0;
    }

    /// <summary>Python's <c>x % 360.0</c>: the result is never negative.</summary>
    private static double Mod360(double x)
    {
        var r = x % 360.0;
        return r < 0 ? r + 360.0 : r + 0.0; // + 0.0 turns -0.0 into 0.0
    }
}
