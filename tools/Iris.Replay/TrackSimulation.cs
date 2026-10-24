using System.Diagnostics;
using System.Globalization;
using System.Text;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Social;
using OpenCvSharp;

namespace Iris.Replay;

/// <summary>
/// What a tracking simulation measured for one clip: how far the tracked brackets were from a fresh detection, how many frames showed a tracked bracket, and what the tracking cost.
/// </summary>
public sealed class TrackingStats
{
    private readonly List<double> _errors = new();
    private readonly List<double> _tickMs = new();
    private readonly List<(double TimeMs, int Shown)> _frames = new();
    private double _firstSeededMs = double.NaN;
    private double _lastSeededMs = double.NaN;

    public TrackingStats(string clip) => Clip = clip;

    public string Clip { get; }

    /// <summary>Frames that were run (every frame the clip gave: tracking runs at the video's frame rate).</summary>
    public int Frames => _frames.Count;

    /// <summary>Detections that were run (at <c>--fps</c>, 5 a second by default).</summary>
    public int Detections { get; private set; }

    /// <summary>Plates that a detection saw and that the one before had seen too: the ones tracking had to carry from one detection to the next.</summary>
    public int Continuing { get; private set; }

    /// <summary>Of those, the ones whose bracket was still shown when the next detection came.</summary>
    public int Kept { get; private set; }

    public int MatchAttempts { get; private set; }

    public int MatchesFound { get; private set; }

    /// <summary>Distances (pixels) between a tracked bracket and the fresh detection of the same plate, one per kept plate and detection.</summary>
    public IReadOnlyList<double> Errors => _errors;

    public void AddDetection(double timeMs, int seeded)
    {
        Detections++;
        if (seeded > 0)
        {
            if (double.IsNaN(_firstSeededMs))
                _firstSeededMs = timeMs;
            _lastSeededMs = timeMs;
        }
    }

    /// <summary>A plate that continues from the last detection: was its bracket still shown, and how far (dx, dy pixels) from where the detection now puts it.</summary>
    public void AddContinuing(bool shown, double dx = 0, double dy = 0)
    {
        Continuing++;
        if (!shown)
            return;
        Kept++;
        _errors.Add(Math.Sqrt(dx * dx + dy * dy));
    }

    public void AddTick(double milliseconds, int attempts, int found)
    {
        _tickMs.Add(milliseconds);
        MatchAttempts += attempts;
        MatchesFound += found;
    }

    /// <summary>One frame: how many brackets the tracking showed after it (the frame's detection counts as shown).</summary>
    public void AddFrame(double timeMs, int shownBrackets) => _frames.Add((timeMs, shownBrackets));

    public double ErrorMedian => Program.Median(_errors.ToList());

    public double ErrorP95 => Program.Percentile95(_errors.ToList());

    public double TickMedianMs => Program.Median(_tickMs.ToList());

    public double TickP95Ms => Program.Percentile95(_tickMs.ToList());

    /// <summary>Share of matches that found their plate (0 to 1), 0 for none.</summary>
    public double FoundShare => MatchAttempts == 0 ? 0.0 : (double)MatchesFound / MatchAttempts;

    /// <summary>Share of continuing plates whose bracket survived to the next detection (0 to 1), 0 for none.</summary>
    public double KeptShare => Continuing == 0 ? 0.0 : (double)Kept / Continuing;

    /// <summary>
    /// The frames of the TRACKING SPAN: from the first detection that saw a confirmed plate to the last one. The share of them that showed at least one tracked bracket
    /// (0 to 1); 0 when no plate was ever seen.
    /// </summary>
    public double ShownShare
    {
        get
        {
            if (double.IsNaN(_firstSeededMs))
                return 0.0;
            var inSpan = _frames.Where(f => f.TimeMs >= _firstSeededMs && f.TimeMs <= _lastSeededMs).ToList();
            return inSpan.Count == 0 ? 0.0 : (double)inSpan.Count(f => f.Shown > 0) / inSpan.Count;
        }
    }

    /// <summary>How many frames the tracking span has.</summary>
    public int SpanFrames =>
        double.IsNaN(_firstSeededMs) ? 0 : _frames.Count(f => f.TimeMs >= _firstSeededMs && f.TimeMs <= _lastSeededMs);

    public const string Header = "clip                     frames  detects  span fr  shown %  kept %  err px med   p95  found %  ms/tick med   p95";

    public string Row(string clipLabel)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Format(inv, "{0,-24} {1,7} {2,8} {3,8} {4,8:0.0} {5,7:0.0} {6,11:0.00} {7,5:0.00} {8,8:0.0} {9,12:0.000} {10,5:0.000}",
            clipLabel.Length <= 24 ? clipLabel : clipLabel[..23] + "~", Frames, Detections, SpanFrames, 100 * ShownShare, 100 * KeptShare,
            ErrorMedian, ErrorP95, 100 * FoundShare, TickMedianMs, TickP95Ms);
    }
}

/// <summary>
/// Simulates the app's detect-then-track on a recording: the detector (with the confirmation tracker) runs at <c>--fps</c> (5 a second by default) and every detection gives the
/// <see cref="PlateTemplateTracker"/> a template, a position and a velocity for each confirmed plate (a detection always wins); on every OTHER frame, at the video's own frame
/// rate, the tracker matches the templates in small search patches and moves the brackets. At each detection the position the tracking had for a plate (moved along its velocity to
/// the time of the frame, as the renderer does) is compared with the fresh detection of the same plate: the distance is the tracking error. The detection result is applied at the
/// frame it was made on (no OCR latency; the app applies it a tick or two later and fast-forwards it by the velocity, which the tracker does too).
/// </summary>
public static class TrackSimulation
{
    public const string CsvHeader = "clip,time_ms,track,dx,dy,error_px";

    /// <summary>Runs one clip's <paramref name="frames"/> (all of them, in order; the images are disposed here) and returns what was measured. <paramref name="errorRows"/> gets a CSV row per comparison.</summary>
    public static TrackingStats RunClip(
        string clip, IEnumerable<SourceFrame> frames, NameplateDetector detector, NameplateDetectorSettings detectorSettings,
        NameplateTrackerSettings trackerSettings, double detectFps, LiveSettings live, ICollection<string>? errorRows = null)
    {
        var stats = new TrackingStats(clip);
        var confirmationTracker = new NameplateTracker(trackerSettings);
        using var tracker = new PlateTemplateTracker(live, detectorSettings);
        using var seedBuilder = new PlateSeedBuilder(detectorSettings);
        var sampler = new FrameSampler(detectFps);
        var lastSeeded = new HashSet<int>();
        double? previousMs = null;
        var inv = CultureInfo.InvariantCulture;

        foreach (var frame in frames)
        {
            using var image = frame.Image;
            var t = frame.TimeMs;
            var width = image.Width;
            var height = image.Height;
            var tickMs = previousMs is null ? 1000.0 / 30.0 : Math.Max(1.0, t - previousMs.Value);
            previousMs = t;

            if (sampler.Due(t))
            {
                sampler.Kept(t);

                // what the tracking showed for each plate just before this detection (moved along its velocity to now, as the renderer does)
                var shown = new Dictionary<int, (double X, double Y)>();
                foreach (var bracket in tracker.Snapshot(t).Brackets)
                {
                    var (dx, dy) = LivePredictor.Shift(bracket, t, live);
                    shown[bracket.TrackId] = (bracket.Box.X + dx, bracket.Box.Y + dy);
                }

                var seeds = Detect(confirmationTracker, detector, seedBuilder, image, frame, live);
                stats.AddDetection(t, seeds.Count);
                var ids = new HashSet<int>();
                foreach (var seed in seeds)
                {
                    ids.Add(seed.TrackId);
                    if (!lastSeeded.Contains(seed.TrackId))
                        continue;
                    if (shown.TryGetValue(seed.TrackId, out var at))
                    {
                        var ex = at.X - seed.PlateBox.X;
                        var ey = at.Y - seed.PlateBox.Y;
                        stats.AddContinuing(true, ex, ey);
                        errorRows?.Add(string.Join(',', clip, t.ToString("0", inv), seed.TrackId.ToString(inv), ex.ToString("0.00", inv), ey.ToString("0.00", inv),
                            Math.Sqrt(ex * ex + ey * ey).ToString("0.00", inv)));
                    }
                    else
                    {
                        stats.AddContinuing(false);
                    }
                }

                lastSeeded = ids;
                tracker.ApplyDetection(MakeResult(seeds, t, width, height), t); // takes over the seeds
            }
            else
            {
                var watch = Stopwatch.StartNew();
                var plan = tracker.Plan(t, tickMs, width, height);
                var patches = new List<Mat?>(plan.Count);
                foreach (var item in plan)
                    patches.Add(new Mat(image, item.Patch).Clone());
                var tick = tracker.Update(t, plan, patches);
                watch.Stop();
                foreach (var patch in patches)
                    patch?.Dispose();
                if (plan.Count > 0)
                    stats.AddTick(watch.Elapsed.TotalMilliseconds, tick.Attempts, tick.Found);
            }

            stats.AddFrame(t, tracker.Snapshot(t).Brackets.Count);
        }

        return stats;
    }

    /// <summary>One detection: the detector and the confirmation tracker over the picture, and a seed for every confirmed plate this picture showed.</summary>
    private static List<PlateSeed> Detect(
        NameplateTracker confirmation, NameplateDetector detector, PlateSeedBuilder seedBuilder, Mat image, SourceFrame frame, LiveSettings live)
    {
        var candidates = detector.FindCandidates(image);
        var evaluator = detector.CreateEvaluator(image, candidates);
        var list = evaluator.TrackCandidates();
        confirmation.Update(frame.Number, frame.TimeMs, list, (top, bottom) =>
        {
            var evaluation = evaluator.Evaluate(top, bottom);
            return new TrackEvaluation(evaluation.IsNameplate, evaluation.Detection?.Name);
        }, image.Height);
        return seedBuilder.Build(confirmation.Tracks, null, image, 0, 0, image.Height, frame.TimeMs, live.MaxPredictionMs, live.MaxTrackedPlates);
    }

    /// <summary>The detection result for the template tracker: a bracket (no names: they do not matter here) for every seed.</summary>
    private static DetectionResult MakeResult(IReadOnlyList<PlateSeed> seeds, double timeMs, int width, int height)
    {
        var brackets = seeds
            .Select(s => new LiveBracket(s.TrackId, "PLATE" + s.TrackId, s.PlateBox, s.VxPerMs, s.VyPerMs, timeMs, MatchKind.NoMatch, OverlayColor.Blue))
            .ToList();
        return new DetectionResult(new LiveSnapshot(brackets, Array.Empty<PanelEntry>(), timeMs), seeds, timeMs, width, height);
    }

    /// <summary>Runs every clip, prints the table and writes tracking.csv (clip, time, track id, dx, dy, error: no names) into the output folder. Returns the exit code.</summary>
    public static int Run(ReplayOptions options, IReadOnlyList<ClipInput> clips, NameplateDetector detector)
    {
        var inv = CultureInfo.InvariantCulture;
        var detectFps = options.SampleFps ?? LiveSettings.Default.DetectionRateHz;
        var live = LiveSettings.Default;
        Console.WriteLine(string.Create(inv,
            $"Tracking simulation: detection at {detectFps:0.##} a second, tracking at every frame of the clip (--every {options.Every}); template match threshold {live.TrackMatchThreshold:0.00}, lost after {live.TrackLostMs:0} ms."));
        Console.WriteLine();

        var rows = new List<string>();
        var all = new List<TrackingStats>();
        Console.WriteLine(TrackingStats.Header);
        foreach (var clip in clips)
        {
            var unreadable = 0;
            TrackingStats stats;
            try
            {
                var frames = FrameSources.Frames(clip, options.Every, _ => unreadable++, options.ImageFps, null);
                stats = RunClip(clip.Clip, frames, detector, options.Settings, options.TrackerSettings, detectFps, live, rows);
            }
            catch (Exception e) when (e is InvalidOperationException or OpenCVException)
            {
                Console.WriteLine($"{clip.Clip}: skipped: {e.Message}");
                continue;
            }

            all.Add(stats);
            Console.WriteLine(stats.Row(clip.Clip));
            if (unreadable > 0)
                Console.WriteLine($"{clip.Clip}: {unreadable} image file(s) could not be read and were skipped");
        }

        var path = Path.Combine(options.OutDirectory, "tracking.csv");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(CsvHeader);
            foreach (var row in rows)
                writer.WriteLine(row);
        }

        if (all.Count > 1)
        {
            var errors = all.SelectMany(s => s.Errors).ToList();
            var span = all.Sum(s => s.SpanFrames);
            var shown = span == 0 ? 0.0 : all.Sum(s => s.ShownShare * s.SpanFrames) / span;
            Console.WriteLine(string.Format(inv, "{0,-24} {1,7} {2,8} {3,8} {4,8:0.0} {5,7:0.0} {6,11:0.00} {7,5:0.00}",
                "ALL", all.Sum(s => s.Frames), all.Sum(s => s.Detections), span, 100 * shown,
                100.0 * all.Sum(s => s.Kept) / Math.Max(1, all.Sum(s => s.Continuing)), Program.Median(errors), Program.Percentile95(errors)));
        }

        Console.WriteLine();
        Console.WriteLine("span fr: frames from the first to the last detection that saw a confirmed plate; shown %: of those, frames that showed a tracked bracket;");
        Console.WriteLine("kept %: plates carried from one detection to the next whose bracket was still shown; err px: distance of the tracked bracket from the fresh detection (median, p95).");
        Console.WriteLine($"Comparisons: {path}");
        return 0;
    }
}
