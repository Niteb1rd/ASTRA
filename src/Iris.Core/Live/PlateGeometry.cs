using Iris.Core.Detection;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>
/// The parts of one nameplate that the overlay cares about, in pixels of whatever picture the track lives in: the NAME ROWS (a name may wrap into several rows,
/// each centered over the others) and the DISTANCE LINE below them. Three boxes follow from it:
/// <list type="bullet">
/// <item><see cref="PlateBox"/>: all of it, the box the brackets are centered on;</item>
/// <item><see cref="NameBox"/>: the name rows only, what the tracking template is cut from (the distance line changes all the time, the name does not);</item>
/// <item>the horizontal extent of the plate box is that of the widest row, because the rows are centered (see <see cref="BracketMetrics"/>).</item>
/// </list>
/// THE ONE SEAM to the detector's track: <see cref="Of"/> reads a <see cref="NameplateTrack"/>: its name rows (<see cref="NameplateTrack.Rows"/>, top to bottom, the name line
/// <see cref="NameplateTrack.Top"/> last; one row for a single-row name) and the distance line (<see cref="NameplateTrack.Bottom"/>).
/// </summary>
public sealed record PlateGeometry(IReadOnlyList<Rect> NameRows, Rect DistanceLine)
{
    /// <summary>The name rows and the distance line of a track, in the track's own (crop) pixels.</summary>
    public static PlateGeometry Of(NameplateTrack track) => new(track.Rows is { Count: > 0 } rows ? rows : new[] { track.Top }, track.Bottom);

    /// <summary>The smallest box around the name rows.</summary>
    public Rect NameBox => Union(NameRows);

    /// <summary>The smallest box around the name rows and the distance line.</summary>
    public Rect PlateBox => Union(NameRows.Append(DistanceLine));

    /// <summary>The plate box as a <see cref="PixelRect"/>, moved by (<paramref name="originX"/>, <paramref name="originY"/>).</summary>
    public PixelRect PlatePixelBox(int originX, int originY)
    {
        var box = PlateBox;
        return new PixelRect(box.X + originX, box.Y + originY, box.Width, box.Height);
    }

    /// <summary>The smallest rectangle holding all <paramref name="rects"/> (an empty rectangle for none).</summary>
    public static Rect Union(IEnumerable<Rect> rects)
    {
        var any = false;
        int left = 0, top = 0, right = 0, bottom = 0;
        foreach (var r in rects)
        {
            if (!any)
            {
                (left, top, right, bottom) = (r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                any = true;
                continue;
            }

            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }

        return any ? new Rect(left, top, right - left, bottom - top) : new Rect();
    }
}
