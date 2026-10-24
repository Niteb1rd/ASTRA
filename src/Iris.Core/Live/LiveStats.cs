using System.Globalization;

namespace Iris.Core.Live;

/// <summary>
/// The numbers behind the once-a-minute log line of the detection loop: how many reads, how long they took (average and 95th percentile), how many read slots were
/// skipped because a read overran its period, how many reads found no picture, and how many confirmed tracks there were. Not thread safe: the loop thread uses it.
/// </summary>
public sealed class LiveStats
{
    private readonly List<double> _readMs = new();
    private int _skippedSlots;
    private int _unavailable;
    private double _windowStartMs;

    public LiveStats(double startMs) => _windowStartMs = startMs;

    public int Reads => _readMs.Count;

    public int SkippedSlots => _skippedSlots;

    public int Unavailable => _unavailable;

    public void AddRead(double milliseconds) => _readMs.Add(milliseconds);

    public void AddSkippedSlots(int count) => _skippedSlots += Math.Max(0, count);

    public void AddUnavailable() => _unavailable++;

    /// <summary>True when <paramref name="everyMs"/> have passed since the start or the last <see cref="TakeSummary"/>.</summary>
    public bool IsDue(double nowMs, double everyMs) => nowMs - _windowStartMs >= everyMs;

    /// <summary>The summary line for the time since the start or the last call, then starts a new window. <paramref name="tracks"/> is the number of confirmed tracks now.</summary>
    public string TakeSummary(double nowMs, int tracks)
    {
        var seconds = Math.Max(0.0, nowMs - _windowStartMs) / 1000.0;
        var inv = CultureInfo.InvariantCulture;
        var average = _readMs.Count == 0 ? 0.0 : _readMs.Average();
        var line = string.Create(inv,
            $"Live detection: {_readMs.Count} reads in {seconds:0} s, average {average:0.0} ms, p95 {Percentile95(_readMs):0.0} ms, {_skippedSlots} slots skipped, {_unavailable} without a picture, {tracks} confirmed tracks");
        _readMs.Clear();
        _skippedSlots = 0;
        _unavailable = 0;
        _windowStartMs = nowMs;
        return line;
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
