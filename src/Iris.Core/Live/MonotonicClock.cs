using System.Diagnostics;

namespace Iris.Core.Live;

/// <summary>
/// A clock that only goes forward, in milliseconds from an arbitrary start: the time base of the detection loop, the tracker and the prediction
/// (the wall clock <see cref="IClock"/> can jump). Tests use their own.
/// </summary>
public interface IMonotonicClock
{
    double NowMs { get; }
}

/// <summary>The real clock: a <see cref="Stopwatch"/>.</summary>
public sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public double NowMs => _watch.Elapsed.TotalMilliseconds;
}
