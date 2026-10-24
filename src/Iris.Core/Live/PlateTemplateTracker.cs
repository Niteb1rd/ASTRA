using Iris.Core.Detection;
using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>One search patch the next tracking tick wants copied from the screen (game-window pixels, clipped to the window).</summary>
/// <param name="TrackId">The plate it is for.</param>
/// <param name="Patch">The template's box, grown on every side by the search margin, clipped to the window.</param>
/// <param name="PredictedX">Where the predicted plate's name box starts (left), game-window pixels.</param>
/// <param name="PredictedY">... and its top.</param>
public sealed record PatchPlan(int TrackId, Rect Patch, double PredictedX, double PredictedY);

/// <summary>What one tracking tick matched: how many plates it looked for in a picture, and how many it found.</summary>
public readonly record struct TrackTick(int Attempts, int Found);

/// <summary>
/// Detect, then track. The detection (a full read of the scan region, 5 times a second) says WHICH plates are on screen and what they are called;
/// this class follows them from picture to picture in between, at the tracking rate (15 to 120 ticks a second), and never reads or changes a name.
/// <list type="bullet">
/// <item><b>Template.</b> Every detection hands over, for each plate it saw, a <see cref="PlateSeed"/>: the colour-masked text of the plate's NAME ROWS (the distance line
/// is left out, it changes), its position, and its velocity. A detection always wins: it replaces position, template and velocity of the plate.</item>
/// <item><b>Tick.</b> <see cref="Plan"/> says which small patches of the screen to copy: for each plate, the template's box moved to the PREDICTED position (last tracked
/// position plus velocity times the time since), grown on every side by max(16 px, 2000 px/s x tick interval), at 1440 high, scaled with the window height.
/// <see cref="Update"/> masks each patch the way the template was masked and runs <c>MatchTemplate</c> (TM_CCORR_NORMED). A best score of at least
/// <see cref="LiveSettings.TrackMatchThreshold"/> (0.65, <see cref="DefaultMatchThreshold"/>) is the new position (to a fraction of a pixel) and updates the velocity from the
/// last few tracked positions; a lower one is "not found this tick": the plate keeps its prediction. A plate not found for <see cref="LiveSettings.TrackLostMs"/> (300 ms)
/// is left out of <see cref="Snapshot"/> (its bracket is hidden) until a detection finds it again.</item>
/// <item>Nameplates have a fixed pixel size at any distance, so there is no scale search.</item>
/// <item>At most <see cref="LiveSettings.MaxTrackedPlates"/> plates are matched; any others are moved by their velocity only.</item>
/// </list>
/// Not thread safe: ONE thread (the capture loop) calls everything. The snapshots it makes are immutable.
/// </summary>
public sealed class PlateTemplateTracker : IDisposable
{
    /// <summary>The best match score (TM_CCORR_NORMED) from which a plate counts as found in a tick.</summary>
    public const double DefaultMatchThreshold = 0.65;

    /// <summary>A velocity is only updated from tracked positions at least this far apart in time (shorter spans are pixel noise).</summary>
    public const double MinVelocitySpanMs = 10.0;

    private sealed class State
    {
        public int TrackId;
        public double X;                 // top-left of the name box (where the template is), as of AtMs
        public double Y;
        public double W;                 // the name box
        public double H;
        public double OffsetX;           // plate box origin minus name box origin
        public double OffsetY;
        public double PlateW;
        public double PlateH;
        public double Vx;                // pixels per millisecond
        public double Vy;
        public double AtMs;              // when (X, Y) was tracked or predicted
        public double LastFoundMs;       // the last time a detection or a match saw the plate
        public Mat? Template;
        public PlateColorClass Class;
        public readonly List<(double Ms, double X, double Y)> Samples = new();
    }

    private readonly LiveSettings _settings;
    private readonly PlateMasker _masker;
    private readonly Dictionary<int, State> _states = new();
    private readonly Mat _patchMask = new();
    private readonly Mat _result = new();
    private LiveSnapshot _base = LiveSnapshot.Empty;
    private int _windowWidth;
    private int _windowHeight;
    private int _visible;
    private bool _disposed;

    public PlateTemplateTracker(LiveSettings? settings = null, NameplateDetectorSettings? detectorSettings = null)
    {
        _settings = settings ?? LiveSettings.Default;
        var problem = _settings.Validate();
        if (problem is not null)
            throw new ArgumentException(problem, nameof(settings));
        _masker = new PlateMasker(detectorSettings);
    }

    /// <summary>The game window size the coordinates belong to (0 before the first detection or plan).</summary>
    public int WindowWidth => _windowWidth;

    public int WindowHeight => _windowHeight;

    /// <summary>How many plates the last <see cref="Snapshot"/> showed.</summary>
    public int VisibleCount => _visible;

    /// <summary>How many plates are followed (shown or not).</summary>
    public int PlateCount => _states.Count;

    /// <summary>How many of them have a template (are matched every tick).</summary>
    public int TemplateCount => _states.Values.Count(s => s.Template is not null);

    /// <summary>Forgets every plate (the game left the front, the window changed).</summary>
    public void Reset()
    {
        foreach (var state in _states.Values)
            state.Template?.Dispose();
        _states.Clear();
        _base = LiveSnapshot.Empty;
        _visible = 0;
    }

    /// <summary>
    /// A detection: its snapshot becomes the base of what is shown (kinds, colors, panel), and every plate it SAW gets its position, template and velocity replaced.
    /// Plates that the snapshot no longer lists are forgotten. Takes OVER the seeds of <paramref name="result"/> (their templates now belong to the tracker, or are disposed
    /// here): do not dispose <paramref name="result"/> afterwards. <paramref name="nowMs"/> is the loop's clock now (only the picture's own time, the seed's, is used for positions).
    /// </summary>
    public void ApplyDetection(DetectionResult result, double nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow(result.WindowWidth, result.WindowHeight);
        _base = result.Snapshot;

        var listed = new HashSet<int>();
        foreach (var bracket in _base.Brackets)
            listed.Add(bracket.TrackId);

        foreach (var id in _states.Keys.Where(id => !listed.Contains(id)).ToList())
        {
            _states[id].Template?.Dispose();
            _states.Remove(id);
        }

        var templates = _states.Values.Count(s => s.Template is not null);
        foreach (var seed in result.Seeds.OrderBy(s => s.TrackId))
        {
            if (!listed.Contains(seed.TrackId) || seed.NameBox.IsEmpty)
            {
                seed.Dispose();
                continue;
            }

            if (!_states.TryGetValue(seed.TrackId, out var state))
            {
                state = new State { TrackId = seed.TrackId };
                _states[seed.TrackId] = state;
            }
            else if (state.Template is not null)
            {
                templates--;
            }

            state.Template?.Dispose();
            state.Template = null;
            var template = seed.Template;
            if (template is not null)
            {
                if (templates < _settings.MaxTrackedPlates)
                {
                    state.Template = template;
                    templates++;
                }
                else
                {
                    template.Dispose();
                }
            }

            state.Class = seed.ColorClass;
            state.X = seed.NameBox.X;
            state.Y = seed.NameBox.Y;
            state.W = seed.NameBox.Width;
            state.H = seed.NameBox.Height;
            state.OffsetX = seed.PlateBox.X - seed.NameBox.X;
            state.OffsetY = seed.PlateBox.Y - seed.NameBox.Y;
            state.PlateW = seed.PlateBox.Width;
            state.PlateH = seed.PlateBox.Height;
            (state.Vx, state.Vy) = ClampVelocity(seed.VxPerMs, seed.VyPerMs);
            state.AtMs = seed.AtMs;
            state.LastFoundMs = seed.AtMs;
            state.Samples.Clear();
            state.Samples.Add((seed.AtMs, state.X, state.Y));
        }
    }

    /// <summary>
    /// The patches the tick at <paramref name="nowMs"/> needs, one per plate that has a template and is shown: the template box at its predicted position, grown by
    /// max(<see cref="LiveSettings.TrackMinMarginPx"/>, <see cref="LiveSettings.TrackMaxSpeedPxPerSec"/> x <paramref name="tickMs"/>) pixels at 1440 high, scaled with the
    /// window height (<paramref name="windowHeight"/>), clipped to the window. Empty when there is nothing to follow.
    /// </summary>
    public IReadOnlyList<PatchPlan> Plan(double nowMs, double tickMs, int windowWidth, int windowHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow(windowWidth, windowHeight);
        var plan = new List<PatchPlan>();
        var margin = Margin(tickMs, windowHeight);
        foreach (var state in _states.Values.OrderBy(s => s.TrackId))
        {
            if (state.Template is null || !IsShown(state, nowMs))
                continue;
            var (px, py) = Predict(state, nowMs, windowHeight);
            var left = Math.Max(0, (int)Math.Floor(px - margin));
            var top = Math.Max(0, (int)Math.Floor(py - margin));
            var right = Math.Min(windowWidth, (int)Math.Ceiling(px + state.W + margin));
            var bottom = Math.Min(windowHeight, (int)Math.Ceiling(py + state.H + margin));
            if (right - left < state.Template.Width || bottom - top < state.Template.Height)
                continue; // the plate is (nearly) off the window: nothing to match in what is left
            plan.Add(new PatchPlan(state.TrackId, new Rect(left, top, right - left, bottom - top), px, py));
        }

        return plan;
    }

    /// <summary>The search margin in pixels: max(16, 2000 px/s x tick) at 1440 high, scaled by <paramref name="windowHeight"/> / 1440.</summary>
    public double Margin(double tickMs, int windowHeight) =>
        Math.Max(_settings.TrackMinMarginPx, _settings.TrackMaxSpeedPxPerSec * tickMs / 1000.0) * BracketMetrics.Scale(windowHeight);

    /// <summary>
    /// One tracking tick at <paramref name="nowMs"/>. <paramref name="patches"/> are the pictures of <paramref name="plan"/>'s patches, from index <paramref name="firstPatch"/> on
    /// (the picture of the detection scan region may come first); null (no picture) when the capture failed. <paramref name="screenUnchanged"/> says that the screen did not
    /// change since the last picture (nothing to match: the plates stand where they were). Every shown plate is matched, or moved by its prediction if it has no
    /// template or no picture; plates that have been lost longer than <see cref="LiveSettings.TrackLostMs"/> are not touched.
    /// </summary>
    public TrackTick Update(double nowMs, IReadOnlyList<PatchPlan> plan, IReadOnlyList<Mat?>? patches, bool screenUnchanged = false, int firstPatch = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var attempts = 0;
        var found = 0;
        var handled = new HashSet<int>();
        var scaleHeight = _windowHeight;

        for (var i = 0; i < plan.Count; i++)
        {
            var item = plan[i];
            if (!_states.TryGetValue(item.TrackId, out var state) || state.Template is null)
                continue;
            handled.Add(item.TrackId);
            var picture = patches is not null && firstPatch + i < patches.Count ? patches[firstPatch + i] : null;
            if (picture is null || picture.Empty())
            {
                if (screenUnchanged)
                    Hold(state, nowMs);
                else
                    Miss(state, nowMs, item.PredictedX, item.PredictedY);
                continue;
            }

            attempts++;
            if (TryMatch(state, picture, item, scaleHeight, out var x, out var y))
            {
                found++;
                Found(state, nowMs, x, y, scaleHeight);
            }
            else
            {
                Miss(state, nowMs, item.PredictedX, item.PredictedY);
            }
        }

        foreach (var state in _states.Values)
        {
            if (handled.Contains(state.TrackId) || !IsShown(state, nowMs))
                continue;
            // no template (or no patch): the plate only moves along its velocity
            if (screenUnchanged)
            {
                Hold(state, nowMs);
                continue;
            }

            var (px, py) = Predict(state, nowMs, scaleHeight);
            Miss(state, nowMs, px, py);
        }

        return new TrackTick(attempts, found);
    }

    /// <summary>
    /// The overlay snapshot at <paramref name="nowMs"/>: one bracket for every plate of the last detection that is followed and was seen (by a detection or a match) within
    /// <see cref="LiveSettings.TrackLostMs"/>; each stands where it was last TRACKED, with its velocity and the time of that position, so that the renderer can move it on to
    /// the moment of a frame (<see cref="LivePredictor"/>). The panel is the detection's own instance.
    /// </summary>
    public LiveSnapshot Snapshot(double nowMs)
    {
        var list = new List<LiveBracket>(_base.Brackets.Count);
        foreach (var bracket in _base.Brackets)
        {
            if (!_states.TryGetValue(bracket.TrackId, out var state) || !IsShown(state, nowMs))
                continue;
            var box = new PixelRect(state.X + state.OffsetX, state.Y + state.OffsetY, state.PlateW, state.PlateH);
            list.Add(new LiveBracket(bracket.TrackId, bracket.Handle, box, state.Vx, state.Vy, state.AtMs, bracket.Kind, bracket.Color));
        }

        _visible = list.Count;
        return new LiveSnapshot(list, _base.Panel, nowMs);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Reset();
        _patchMask.Dispose();
        _result.Dispose();
        _masker.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ internals

    private bool IsShown(State state, double nowMs) => nowMs - state.LastFoundMs <= _settings.TrackLostMs;

    private void EnsureWindow(int width, int height)
    {
        if (width == _windowWidth && height == _windowHeight)
            return;
        // other window size, other coordinates: nothing known is valid any more (a first call only sets the size)
        if (_windowWidth != 0 || _windowHeight != 0)
            Reset();
        _windowWidth = width;
        _windowHeight = height;
    }

    private double MaxSpeedPxPerMs(int windowHeight) => _settings.TrackMaxSpeedPxPerSec * BracketMetrics.Scale(windowHeight) / 1000.0;

    private (double X, double Y) ClampVelocity(double vx, double vy)
    {
        var limit = MaxSpeedPxPerMs(_windowHeight);
        var speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed <= limit || speed <= 0)
            return (vx, vy);
        var k = limit / speed;
        return (vx * k, vy * k);
    }

    /// <summary>Last tracked position plus velocity times the time since it (at most <see cref="LiveSettings.MaxPredictionMs"/>).</summary>
    private (double X, double Y) Predict(State state, double nowMs, int windowHeight)
    {
        var dt = Math.Min(Math.Max(0.0, nowMs - state.AtMs), _settings.MaxPredictionMs);
        return (state.X + state.Vx * dt, state.Y + state.Vy * dt);
    }

    private bool TryMatch(State state, Mat picture, PatchPlan item, int scaleHeight, out double x, out double y)
    {
        x = 0;
        y = 0;
        var template = state.Template!;
        if (picture.Width < template.Width || picture.Height < template.Height)
            return false;

        _masker.Build(picture, scaleHeight, state.Class, _patchMask);
        Cv2.MatchTemplate(_patchMask, template, _result, TemplateMatchModes.CCorrNormed);
        Cv2.MinMaxLoc(_result, out _, out var best, out _, out var at);
        if (!(best >= _settings.TrackMatchThreshold)) // also false for NaN
            return false;

        x = item.Patch.X + at.X + SubPixel(at, horizontal: true);
        y = item.Patch.Y + at.Y + SubPixel(at, horizontal: false);
        return true;
    }

    /// <summary>The offset (-0.5 to 0.5) of the peak's true place from the best whole pixel: the vertex of a parabola through the score and its two neighbours.</summary>
    private double SubPixel(Point at, bool horizontal)
    {
        var index = horizontal ? at.X : at.Y;
        var size = horizontal ? _result.Cols : _result.Rows;
        if (index <= 0 || index >= size - 1)
            return 0.0;
        var centre = _result.At<float>(at.Y, at.X);
        var before = horizontal ? _result.At<float>(at.Y, at.X - 1) : _result.At<float>(at.Y - 1, at.X);
        var after = horizontal ? _result.At<float>(at.Y, at.X + 1) : _result.At<float>(at.Y + 1, at.X);
        var curvature = before - 2.0 * centre + after;
        if (!(curvature < -1e-6))
            return 0.0;
        var offset = 0.5 * (before - after) / curvature;
        return Math.Clamp(offset, -0.5, 0.5);
    }

    private void Found(State state, double nowMs, double x, double y, int windowHeight)
    {
        state.X = x;
        state.Y = y;
        state.AtMs = nowMs;
        state.LastFoundMs = nowMs;
        state.Samples.Add((nowMs, x, y));
        while (state.Samples.Count > _settings.TrackVelocitySamples)
            state.Samples.RemoveAt(0);
        if (state.Samples.Count < 2)
            return;
        var first = state.Samples[0];
        var last = state.Samples[^1];
        var span = last.Ms - first.Ms;
        if (span < MinVelocitySpanMs)
            return; // too short to tell a speed from the pixel noise: the old velocity stands
        (state.Vx, state.Vy) = ClampVelocity((last.X - first.X) / span, (last.Y - first.Y) / span);
    }

    private static void Miss(State state, double nowMs, double predictedX, double predictedY)
    {
        state.X = predictedX;
        state.Y = predictedY;
        state.AtMs = nowMs;
        state.Samples.Clear(); // predictions are no measurements
    }

    private static void Hold(State state, double nowMs)
    {
        // the screen did not change: nothing moved, so nothing is moving
        state.AtMs = nowMs;
        state.LastFoundMs = nowMs;
        state.Vx = 0;
        state.Vy = 0;
        state.Samples.Clear();
        state.Samples.Add((nowMs, state.X, state.Y));
    }
}
