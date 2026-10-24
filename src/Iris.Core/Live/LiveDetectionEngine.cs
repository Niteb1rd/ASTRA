using System.Data.Common;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Iris.Core.Overlay;
using Iris.Core.Social;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>How one <see cref="LiveDetectionEngine.Step"/> went.</summary>
public enum StepStatus
{
    /// <summary>A picture was read, the detector and the tracker ran, and <see cref="StepResult.Snapshot"/> is new.</summary>
    Read,

    /// <summary>No picture could be used (see <see cref="StepResult.Message"/>). The snapshot is the previous one; it hides itself once it is too old (<see cref="LiveSettings.StaleHideMs"/>).</summary>
    Skipped,
}

/// <summary>What one step found: its status, the snapshot (the previous one when skipped) and, when skipped, why.</summary>
public sealed record StepResult(StepStatus Status, LiveSnapshot Snapshot, string? Message = null);

/// <summary>
/// One read of the detection loop, without any threads, timers or windows (those are the app's): capture a picture from an <see cref="IFrameSource"/>, run the
/// <see cref="NameplateDetector"/> and the <see cref="NameplateTracker"/> over it (the pipeline shape of the replay tool), feed the
/// <see cref="DuplicateNameTracker"/>, ask for lookups of org-list members, and build the <see cref="LiveSnapshot"/> that the overlay shows
/// (<see cref="LiveSnapshotBuilder"/>). Not thread safe: ONE thread calls <see cref="Step"/>; the snapshot it returns is immutable and may be read from others.
/// </summary>
public sealed class LiveDetectionEngine : IDisposable
{
    private readonly NameplateReader _reader;
    private readonly Func<string, IReadOnlyList<OrgMembership>?> _lookupOrgs;
    private readonly DuplicateNameTracker? _duplicates;
    private readonly OrgMemberLookupQueue? _lookups;
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly LiveSettings _settings;
    private readonly NameplateDetectorSettings _baseDetectorSettings;
    private readonly NameplateTrackerSettings _trackerSettings;
    private readonly LiveSnapshotBuilder _builder;
    private readonly PlateSeedBuilder _seedBuilder;
    private readonly Dictionary<string, (double AtMs, IReadOnlyList<OrgMembership>? Orgs)> _orgMemo = new(StringComparer.OrdinalIgnoreCase);

    private NameplateDetector? _detector;
    private ScanGeometry? _geometry;
    private NameplateTracker _tracker;
    private LiveSnapshot _snapshot = LiveSnapshot.Empty;
    private long _frame;
    private double _memoNowMs;
    private bool _disposed;

    /// <param name="reader">The letter reader (it may be shared; the model is read only).</param>
    /// <param name="exclusions">The exclusion list.</param>
    /// <param name="lookupOrgs">Orgs of a handle from the cache, null when never looked up. The engine remembers answers for <see cref="LiveSettings.OrgCacheMs"/>.</param>
    /// <param name="duplicates">Counts names seen on two separate plates at once (auto exclusion), or null for none.</param>
    /// <param name="lookups">Queues lookups of org-list members, or null for none.</param>
    public LiveDetectionEngine(
        NameplateReader reader,
        ExclusionList exclusions,
        Func<string, IReadOnlyList<OrgMembership>?> lookupOrgs,
        DuplicateNameTracker? duplicates = null,
        OrgMemberLookupQueue? lookups = null,
        LiveSettings? settings = null,
        NameplateDetectorSettings? detectorSettings = null,
        NameplateTrackerSettings? trackerSettings = null,
        ILog? log = null,
        IClock? clock = null)
    {
        _settings = settings ?? LiveSettings.Default;
        var problem = _settings.Validate();
        if (problem is not null)
            throw new ArgumentException(problem, nameof(settings));
        _reader = reader;
        _lookupOrgs = lookupOrgs;
        _duplicates = duplicates;
        _lookups = lookups;
        _log = log ?? NullLog.Instance;
        _clock = clock ?? SystemClock.Instance;
        _baseDetectorSettings = detectorSettings ?? NameplateDetectorSettings.Default;
        _trackerSettings = trackerSettings ?? NameplateTrackerSettings.Default;
        _builder = new LiveSnapshotBuilder(exclusions, MemoizedOrgs, _settings);
        _seedBuilder = new PlateSeedBuilder(_baseDetectorSettings);
        _tracker = new NameplateTracker(_trackerSettings);
    }

    /// <summary>The tracker (for tests and diagnostics).</summary>
    public NameplateTracker Tracker => _tracker;

    /// <summary>The geometry of the current game window size; null before the first step.</summary>
    public ScanGeometry? Geometry => _geometry;

    /// <summary>The latest snapshot (<see cref="LiveSnapshot.Empty"/> after <see cref="Reset"/>).</summary>
    public LiveSnapshot Snapshot => _snapshot;

    /// <summary>
    /// One read. <paramref name="window"/> is the game window in screen pixels, <paramref name="nowMs"/> the loop's monotonic clock (the tracker's time base:
    /// it must not go backwards between steps). A window of another size than before starts a new tracker (coordinates mean something else).
    /// Exceptions of the image code (<see cref="OpenCVException"/>) are not caught here; the loop logs them and goes on.
    /// </summary>
    public StepResult Step(IFrameSource source, PixelRect window, SocialConfig config, double nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var width = (int)Math.Round(window.Width);
        var height = (int)Math.Round(window.Height);
        if (_geometry is null || !_geometry.Matches(width, height))
        {
            if (!SetGeometry(width, height))
                return new StepResult(StepStatus.Skipped, _snapshot, $"the game window ({width}x{height}) is too small to scan");
        }

        var geometry = _geometry!;
        var region = geometry.ToScreen((int)Math.Round(window.X), (int)Math.Round(window.Y));
        var grab = source.Grab(region);
        if (grab.Status == GrabStatus.Unavailable || grab.Image is null)
            return new StepResult(StepStatus.Skipped, _snapshot, grab.Message ?? "no picture");

        var result = Process(grab.Image, window, config, nowMs, out var problem, buildSeeds: false);
        return result is null
            ? new StepResult(StepStatus.Skipped, _snapshot, problem)
            : new StepResult(StepStatus.Read, result.Snapshot);
    }

    /// <summary>
    /// The geometry (what to copy, and the detector settings for the copy) for a game window of this size, or false when the window is too small. It does not
    /// depend on anything the engine has seen, so the capture loop can ask for it before the first read.
    /// </summary>
    public bool TryGetGeometry(int windowWidth, int windowHeight, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ScanGeometry? geometry) =>
        ScanGeometry.TryCreate(windowWidth, windowHeight, _baseDetectorSettings, out geometry);

    /// <summary>
    /// One read of a picture that is already there: <paramref name="image"/> is the scan region's crop of the game window (<see cref="ScanGeometry.Capture"/>,
    /// the size <see cref="TryGetGeometry"/> gives), taken at <paramref name="captureMs"/> on the loop's monotonic clock (the tracker's time base: it must not go back
    /// between reads). The detector, the tracker, the duplicate-name check and the snapshot as in <see cref="Step"/>; and, with <paramref name="buildSeeds"/>, the
    /// <see cref="PlateSeed"/> of every confirmed plate this picture showed (its template cut out of <paramref name="image"/>), for the template tracking.
    /// Returns null, and says why in <paramref name="problem"/>, when the picture cannot be used (a window too small to scan, a picture of the wrong size).
    /// The caller owns the result (dispose it, or hand it to <see cref="PlateTemplateTracker.ApplyDetection"/>, which takes over its seeds).
    /// </summary>
    public DetectionResult? Process(Mat image, PixelRect window, SocialConfig config, double captureMs, out string? problem, bool buildSeeds = true, int epoch = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        problem = null;
        _memoNowMs = captureMs;

        var width = (int)Math.Round(window.Width);
        var height = (int)Math.Round(window.Height);
        if (_geometry is null || !_geometry.Matches(width, height))
        {
            if (!SetGeometry(width, height))
            {
                problem = $"the game window ({width}x{height}) is too small to scan";
                return null;
            }
        }

        var geometry = _geometry!;
        if (image.Width != geometry.Capture.Width || image.Height != geometry.Capture.Height)
        {
            problem = $"the picture is {image.Width}x{image.Height}, expected {geometry.Capture.Width}x{geometry.Capture.Height}";
            return null;
        }

        _frame++;
        var detector = _detector!;
        var candidates = detector.FindCandidates(image);
        var evaluator = detector.CreateEvaluator(image, candidates);
        var evaluated = new Dictionary<Rect, PairEvaluation>();
        _tracker.Update(_frame, captureMs, evaluator.TrackCandidates(), (top, bottom) =>
        {
            var evaluation = evaluator.Evaluate(top, bottom);
            evaluated[top] = evaluation;
            return new TrackEvaluation(evaluation.IsNameplate, evaluation.Detection?.Name);
        }, geometry.WindowHeight);

        CountDuplicates(config, captureMs, evaluated);
        _snapshot = _builder.Build(_tracker.Tracks, config, geometry.Capture.X, geometry.Capture.Y, captureMs, _snapshot);
        if (_lookups is not null)
        {
            foreach (var bracket in _snapshot.Brackets)
                _lookups.Consider(bracket.Handle, config, captureMs);
        }

        IReadOnlyList<PlateSeed> seeds = Array.Empty<PlateSeed>();
        if (buildSeeds && _snapshot.Brackets.Count > 0)
        {
            var shown = new HashSet<int>(_snapshot.Brackets.Select(b => b.TrackId));
            seeds = _seedBuilder.Build(
                _tracker.Tracks, shown, image, geometry.Capture.X, geometry.Capture.Y, geometry.WindowHeight, captureMs,
                _settings.MaxPredictionMs, _settings.MaxTrackedPlates);
        }

        return new DetectionResult(_snapshot, seeds, captureMs, width, height, epoch);
    }

    /// <summary>
    /// Forgets everything seen so far: the tracks, the snapshot, the remembered orgs and the waiting lookups. Called when detection stops (the game left the front,
    /// the overlay was disabled): what was on screen then is not what is on screen later.
    /// </summary>
    public void Reset()
    {
        _tracker = new NameplateTracker(_trackerSettings);
        _snapshot = LiveSnapshot.Empty;
        _orgMemo.Clear();
        _lookups?.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _detector?.Dispose();
        _detector = null;
        _seedBuilder.Dispose();
    }

    private bool SetGeometry(int width, int height)
    {
        if (!ScanGeometry.TryCreate(width, height, _baseDetectorSettings, out var geometry))
            return false;
        _detector?.Dispose();
        _detector = new NameplateDetector(_reader, geometry.DetectorSettings);
        _geometry = geometry;
        Reset(); // other window, other coordinates
        return true;
    }

    /// <summary>
    /// The duplicate name rule: a name on two separate plates in one frame counts that frame (three frames: the name goes on the exclusion list). The plates are the
    /// Nameplate tracks seen in this frame, with their confirmed name, or the name read in this frame while the track is not confirmed yet.
    /// </summary>
    private void CountDuplicates(SocialConfig config, double nowMs, Dictionary<Rect, PairEvaluation> evaluated)
    {
        if (_duplicates is null)
            return;
        List<DetectedNameplate>? plates = null;
        foreach (var track in _tracker.Tracks)
        {
            if (track.Verdict != TrackVerdict.Nameplate || track.LastSeenMs != nowMs)
                continue;
            var name = track.ConfirmedName;
            if (name is null && evaluated.TryGetValue(track.Top, out var evaluation))
                name = evaluation.Detection?.Name;
            if (name is null)
                continue;
            var box = LiveSnapshotBuilder.WindowBox(track, 0, 0);
            (plates ??= new List<DetectedNameplate>()).Add(new DetectedNameplate(name, (int)box.X, (int)box.Y, (int)box.Width, (int)box.Height));
        }

        if (plates is null || plates.Count < 2)
            return;
        try
        {
            var added = _duplicates.Process(_frame, plates, config);
            foreach (var name in added)
                _log.Write(_clock.UtcNow, $"live: {name} was seen on 2+ separate nameplates in {DuplicateNameTracker.FramesNeeded} frames and was added to the exclusion list");
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            _log.Write(_clock.UtcNow, "live: the duplicate name check failed: " + ex.Message);
        }
    }

    private IReadOnlyList<OrgMembership>? MemoizedOrgs(string handle)
    {
        if (_orgMemo.TryGetValue(handle, out var known) && _memoNowMs - known.AtMs < _settings.OrgCacheMs)
            return known.Orgs;
        IReadOnlyList<OrgMembership>? orgs;
        try
        {
            orgs = _lookupOrgs(handle);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidDataException)
        {
            orgs = null; // a cache problem must not stop the overlay: the player shows as "not looked up"
        }

        if (_orgMemo.Count >= 256)
            _orgMemo.Clear();
        _orgMemo[handle] = (_memoNowMs, orgs);
        return orgs;
    }
}
