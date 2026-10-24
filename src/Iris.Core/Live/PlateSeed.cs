using Iris.Core.Detection;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>
/// What one detection tells the template tracking about one confirmed plate: where the plate was in the picture the detection read (game-window pixels), its
/// template (the colour-masked text of its NAME ROWS, tight to the text) and the velocity the detector saw it move with. A detection always wins: the
/// tracker takes all of this in place of what it had. The seed owns its <see cref="Template"/>; the tracker takes it over (see <see cref="PlateTemplateTracker.ApplyDetection"/>).
/// </summary>
/// <param name="TrackId">The id of the detector's <see cref="NameplateTrack"/> (the key that ties the seed to a <see cref="LiveBracket"/>).</param>
/// <param name="PlateBox">The whole plate (name rows and distance line), game-window pixels, at <paramref name="AtMs"/>.</param>
/// <param name="NameBox">The name rows' box, game-window pixels: the template's place at <paramref name="AtMs"/>.</param>
/// <param name="Template">The text mask of the name rows (8 bit, one channel, <paramref name="NameBox"/>'s size), or null when the text had no ink.</param>
/// <param name="ColorClass">The colour class the template was made with; the search patches are masked with the same one.</param>
/// <param name="VxPerMs">The plate's speed as the detector saw it (pixels per millisecond).</param>
/// <param name="AtMs">When the picture was taken (the loop's monotonic clock).</param>
public sealed record PlateSeed(
    int TrackId, PixelRect PlateBox, PixelRect NameBox, Mat? Template, PlateColorClass ColorClass, double VxPerMs, double VyPerMs, double AtMs) : IDisposable
{
    public void Dispose() => Template?.Dispose();
}

/// <summary>What one detection (one read of the scan region) gave: the snapshot (kinds, colors, panel), the seeds of the plates it saw, and when its picture was taken.</summary>
public sealed class DetectionResult : IDisposable
{
    public DetectionResult(LiveSnapshot snapshot, IReadOnlyList<PlateSeed> seeds, double captureMs, int windowWidth, int windowHeight, int epoch = 0)
    {
        Snapshot = snapshot;
        Seeds = seeds;
        CaptureMs = captureMs;
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        Epoch = epoch;
    }

    /// <summary>The overlay model as of this read. Its brackets carry the plate boxes as the detector saw them.</summary>
    public LiveSnapshot Snapshot { get; }

    /// <summary>One seed per confirmed plate that this read SAW (a plate that was only kept alive by its track, without being seen, has none).</summary>
    public IReadOnlyList<PlateSeed> Seeds { get; }

    /// <summary>When the picture was taken (the loop's monotonic clock): the tracker's time base.</summary>
    public double CaptureMs { get; }

    public int WindowWidth { get; }

    public int WindowHeight { get; }

    /// <summary>Which run of the capture loop asked for it: a result of an earlier run (the game left the front meanwhile) is dropped.</summary>
    public int Epoch { get; }

    /// <summary>A result with nothing in it (no seeds, an empty snapshot).</summary>
    public static DetectionResult Empty(double captureMs, int windowWidth, int windowHeight, int epoch = 0) =>
        new(LiveSnapshot.Empty, Array.Empty<PlateSeed>(), captureMs, windowWidth, windowHeight, epoch);

    /// <summary>The same result stamped with another epoch (the seeds are shared: dispose only one of the two).</summary>
    public DetectionResult WithEpoch(int epoch) => new(Snapshot, Seeds, CaptureMs, WindowWidth, WindowHeight, epoch);

    public void Dispose()
    {
        foreach (var seed in Seeds)
            seed.Dispose();
    }
}

/// <summary>
/// Makes the seeds of the plates that a detection saw: for each confirmed track the name rows' text mask, cut out of the very picture the detection read,
/// with the colour class of that text (or <see cref="PlateColorClass.Any"/> when no class stands out: then the detector's own combined mask is used).
/// The distance line is left out of the template on purpose: its digits change all the time. Not thread safe (it owns a <see cref="PlateMasker"/>).
/// </summary>
public sealed class PlateSeedBuilder : IDisposable
{
    private readonly PlateMasker _masker;
    private readonly Mat _mask = new();
    private bool _disposed;

    public PlateSeedBuilder(NameplateDetectorSettings? settings = null) => _masker = new PlateMasker(settings);

    /// <summary>The masker (the tracker uses its own; the builder's is for the templates).</summary>
    public PlateMasker Masker => _masker;

    /// <summary>
    /// A seed for each track of <paramref name="tracks"/> that the read at <paramref name="captureMs"/> saw (its <see cref="NameplateTrack.LastSeenMs"/> is that time)
    /// and, when <paramref name="onlyIds"/> is given, whose id is in it. <paramref name="image"/> is the picture the detector read (a crop of the game window whose
    /// top-left corner is at (<paramref name="originX"/>, <paramref name="originY"/>) in game-window pixels, the tracks' coordinates are in crop pixels);
    /// <paramref name="scaleHeight"/> is the game window's height. At most <paramref name="maxTemplates"/> seeds get a template (the lowest track ids first); the rest are
    /// seeds without one (the tracker moves them by their velocity only).
    /// </summary>
    public List<PlateSeed> Build(
        IReadOnlyList<NameplateTrack> tracks, ISet<int>? onlyIds, Mat image, int originX, int originY, int scaleHeight, double captureMs,
        double maxVelocityDtMs, int maxTemplates)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var seeds = new List<PlateSeed>();
        var made = 0;
        foreach (var track in tracks.OrderBy(t => t.Id))
        {
            if (track.Verdict != TrackVerdict.Nameplate || track.ConfirmedName is null || track.LastSeenMs != captureMs)
                continue;
            if (onlyIds is not null && !onlyIds.Contains(track.Id))
                continue;

            var plate = PlateGeometry.Of(track);
            var nameBox = plate.NameBox;
            var motion = track.Motion(maxVelocityDtMs);
            Mat? template = null;
            var cls = PlateColorClass.Any;
            if (made < maxTemplates)
            {
                template = MakeTemplate(image, nameBox, scaleHeight, out cls);
                if (template is not null)
                    made++;
            }

            seeds.Add(new PlateSeed(
                track.Id,
                plate.PlatePixelBox(originX, originY),
                new PixelRect(nameBox.X + originX, nameBox.Y + originY, nameBox.Width, nameBox.Height),
                template, cls, motion.VxPerMs, motion.VyPerMs, captureMs));
        }

        return seeds;
    }

    /// <summary>
    /// The text mask of <paramref name="box"/> (crop pixels) of <paramref name="image"/>: the mask is made on the box with a margin of half a top-hat window
    /// around it (so that the box's edge pixels see their surroundings like in the detector's mask), then the box is cut out and cloned. Null when the box is
    /// empty or has no text pixel at all.
    /// </summary>
    public Mat? MakeTemplate(Mat image, Rect box, int scaleHeight, out PlateColorClass cls)
    {
        cls = PlateColorClass.Any;
        var bounds = new Rect(0, 0, image.Width, image.Height);
        var inside = PlateMasker.Intersect(box, bounds);
        if (inside.Width <= 0 || inside.Height <= 0)
            return null;

        var margin = _masker.KernelSize(scaleHeight) / 2 + 2;
        var context = PlateMasker.Intersect(new Rect(inside.X - margin, inside.Y - margin, inside.Width + 2 * margin, inside.Height + 2 * margin), bounds);
        using var crop = new Mat(image, context);
        var local = new Rect(inside.X - context.X, inside.Y - context.Y, inside.Width, inside.Height);

        cls = _masker.Classify(crop, scaleHeight, local);
        _masker.Build(crop, scaleHeight, cls, _mask);
        using var part = new Mat(_mask, local);
        if (Cv2.CountNonZero(part) == 0)
            return null;
        return part.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mask.Dispose();
        _masker.Dispose();
    }
}
