using System.Globalization;

namespace Iris.Core.Live;

/// <summary>
/// The numbers behind the once-a-minute log line of the live loop: how many reads (detections), how long they took (average and 95th percentile), how many read slots were
/// skipped because the detection was still busy, how many reads found no picture, and how many confirmed tracks there were; and, for the tracking between reads, how many tracking
/// ticks ran, how long a tick took (average and 95th percentile, milliseconds of tracking work, without the wait for the screen) and the share of matches found
/// (plates found / plates looked for). The detection thread and the capture loop add to it, one of them takes the summary: every method takes the lock.
/// </summary>
public sealed class LiveStats
{
    private readonly object _gate = new();
    private readonly List<double> _readMs = new();
    private readonly List<double> _trackMs = new();
    private int _skippedSlots;
    private int _unavailable;
    private long _matchAttempts;
    private long _matchesFound;
    private double _windowStartMs;
    private int _gameFrames;            // new frames of the game seen (every acquired frame with new content, tracked or not)
    private double _firstGameFrameMs;
    private double _lastGameFrameMs;
    private int _trackedFrames;         // frames the tracking ran for

    public LiveStats(double startMs) => _windowStartMs = startMs;

    /// <summary>The frame rate of the game as seen by the capture, from the timestamps of the new frames: (frames - 1) / (last - first), 0 with fewer than two frames.</summary>
    public static double FrameRate(int frames, double firstMs, double lastMs)
    {
        var span = lastMs - firstMs;
        return frames < 2 || !(span > 0) ? 0.0 : (frames - 1) * 1000.0 / span;
    }

    /// <summary>A new frame of the game was seen at <paramref name="frameMs"/> (the frame's own time, live clock), whether the tracking ran for it or not.</summary>
    public void AddGameFrame(double frameMs)
    {
        lock (_gate)
        {
            if (_gameFrames == 0)
                _firstGameFrameMs = frameMs;
            _lastGameFrameMs = frameMs;
            _gameFrames++;
        }
    }

    /// <summary>The tracking ran for a frame (with or without plates to follow).</summary>
    public void AddTrackedFrame()
    {
        lock (_gate)
            _trackedFrames++;
    }

    public int Reads
    {
        get
        {
            lock (_gate)
                return _readMs.Count;
        }
    }

    public int SkippedSlots
    {
        get
        {
            lock (_gate)
                return _skippedSlots;
        }
    }

    public int Unavailable
    {
        get
        {
            lock (_gate)
                return _unavailable;
        }
    }

    public int TrackTicks
    {
        get
        {
            lock (_gate)
                return _trackMs.Count;
        }
    }

    public void AddRead(double milliseconds)
    {
        lock (_gate)
            _readMs.Add(milliseconds);
    }

    public void AddSkippedSlots(int count)
    {
        lock (_gate)
            _skippedSlots += Math.Max(0, count);
    }

    public void AddUnavailable()
    {
        lock (_gate)
            _unavailable++;
    }

    /// <summary>One tracking tick: the milliseconds the tracking work took, how many plates were looked for in a picture and how many of them were found.</summary>
    public void AddTrackTick(double milliseconds, int attempts, int found)
    {
        lock (_gate)
        {
            _trackMs.Add(milliseconds);
            _matchAttempts += Math.Max(0, attempts);
            _matchesFound += Math.Max(0, found);
        }
    }

    /// <summary>True when <paramref name="everyMs"/> have passed since the start or the last <see cref="TakeSummary"/>.</summary>
    public bool IsDue(double nowMs, double everyMs)
    {
        lock (_gate)
            return nowMs - _windowStartMs >= everyMs;
    }

    /// <summary>
    /// The summary line for the time since the start or the last call, then starts a new window. <paramref name="tracks"/> is the number of confirmed tracks now.
    /// <paramref name="trackingCapFps"/> is the tracking cap in frames a second (0 = Max, no cap); the line says how many frames the game showed
    /// ("game frames ~143/s", from the new-frame timestamps) and how many of them were tracked.
    /// </summary>
    public string TakeSummary(double nowMs, int tracks, int trackingCapFps = 0)
    {
        lock (_gate)
        {
            var seconds = Math.Max(0.0, nowMs - _windowStartMs) / 1000.0;
            var inv = CultureInfo.InvariantCulture;
            var average = _readMs.Count == 0 ? 0.0 : _readMs.Average();
            var trackAverage = _trackMs.Count == 0 ? 0.0 : _trackMs.Average();
            var share = _matchAttempts == 0 ? 0.0 : 100.0 * _matchesFound / _matchAttempts;
            var gameRate = FrameRate(_gameFrames, _firstGameFrameMs, _lastGameFrameMs);
            var trackedRate = seconds > 0 ? _trackedFrames / seconds : 0.0;
            var cap = trackingCapFps > 0 ? $"cap {trackingCapFps}" : "no cap";
            var line = string.Create(inv,
                $"Live detection: {_readMs.Count} reads in {seconds:0} s, average {average:0.0} ms, p95 {Percentile95(_readMs):0.0} ms, {_skippedSlots} slots skipped, {_unavailable} without a picture, {tracks} confirmed tracks; "
                + $"tracking: {_trackMs.Count} ticks, average {trackAverage:0.000} ms, p95 {Percentile95(_trackMs):0.000} ms, {share:0.0} % of {_matchAttempts} matches found; "
                + $"game frames ~{gameRate:0}/s, tracked ~{trackedRate:0}/s ({cap})");
            _gameFrames = 0;
            _trackedFrames = 0;
            _readMs.Clear();
            _trackMs.Clear();
            _skippedSlots = 0;
            _unavailable = 0;
            _matchAttempts = 0;
            _matchesFound = 0;
            _windowStartMs = nowMs;
            return line;
        }
    }

    /// <summary>95th percentile by nearest rank, 0 for no values.</summary>
    public static double Percentile95(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0.0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}

/// <summary>
/// The cost of rendering at monitor refresh, for the once-a-minute log line: how many frames the overlay saw (one per refresh while the brackets are shown), how many of
/// them redrew the brackets (a bracket moved by a whole pixel) and what a frame cost on average (milliseconds of work on the UI thread to find out and to ask for the redraw).
/// UI thread only.
/// </summary>
public sealed class RenderStats
{
    private int _frames;
    private int _redraws;
    private double _totalMs;
    private double _windowStartMs;

    public RenderStats(double startMs) => _windowStartMs = startMs;

    public int Frames => _frames;

    public int Redraws => _redraws;

    public void AddFrame(double milliseconds, bool redrew)
    {
        _frames++;
        if (redrew)
            _redraws++;
        _totalMs += milliseconds;
    }

    public bool IsDue(double nowMs, double everyMs) => nowMs - _windowStartMs >= everyMs;

    /// <summary>The summary line for the time since the start or the last call, then starts a new window.</summary>
    public string TakeSummary(double nowMs)
    {
        var seconds = Math.Max(0.0, nowMs - _windowStartMs) / 1000.0;
        var average = _frames == 0 ? 0.0 : _totalMs / _frames;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"Overlay render: {_frames} frames in {seconds:0} s, {_redraws} redraws, average {average:0.000} ms per frame");
        _frames = 0;
        _redraws = 0;
        _totalMs = 0;
        _windowStartMs = nowMs;
        return line;
    }
}
