namespace Iris.Core.Live;

/// <summary>
/// The cap of the tracking, as a pure decision (no clock, no threads): the capture loop wakes for every NEW frame of the game (Desktop Duplication tells it when the
/// screen has a new picture) and asks whether this frame is to be tracked. A frame is tracked when at least one tracking period has passed since the last tracked one,
/// less <see cref="SlackMs"/> so that a game running at exactly the cap (or a jittery frame time) is not tracked every second frame; with no cap (Max) every new frame is.
/// </summary>
public static class TrackingCap
{
    /// <summary>The frame time may be this much short of one period and still count: frame times jitter by a fraction of a millisecond, and a 60 fps game at a cap of 60 must track every frame.</summary>
    public const double SlackMs = 1.0;

    /// <summary>The shortest time between two tracked frames for a cap in frames a second (0 for no cap, or a cap of 0 or less).</summary>
    public static double MinIntervalMs(int capFps) => capFps <= 0 ? 0.0 : Math.Max(0.0, 1000.0 / capFps - SlackMs);

    /// <summary>
    /// Whether the new frame at <paramref name="frameMs"/> is to be tracked, given when the last tracked frame was (<paramref name="lastTrackedMs"/>; NaN when there was none yet,
    /// then the answer is yes) and the cap (<paramref name="capFps"/> frames a second; 0 or less means Max: every new frame).
    /// </summary>
    public static bool ShouldTrack(double frameMs, double lastTrackedMs, int capFps)
    {
        if (capFps <= 0 || double.IsNaN(lastTrackedMs))
            return true;
        return frameMs - lastTrackedMs >= MinIntervalMs(capFps);
    }
}

/// <summary>
/// Frame times on the live clock (<see cref="StopwatchClock"/>, milliseconds from its start). Desktop Duplication stamps a frame with <c>LastPresentTime</c>, in QueryPerformanceCounter
/// ticks, which are the ticks of <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> on Windows; the clock's origin in those ticks was taken when the clock started.
/// </summary>
public static class FrameClock
{
    /// <summary>A present time this far AHEAD of the moment the frame was acquired cannot be right (the frame is older than its acquisition): the clock reading is used instead.</summary>
    public const double MaxAheadMs = 2.0;

    /// <summary>A present time further BEHIND the acquisition than this is not a time of this frame (a driver that reports something else): the clock reading is used instead.</summary>
    public const double MaxBehindMs = 1000.0;

    /// <summary>Counter ticks to live-clock milliseconds: <c>(ticks - originTicks) * 1000 / frequency</c>. NaN for a frequency of 0 or less.</summary>
    public static double QpcToMs(long qpcTicks, long originTicks, long frequency)
    {
        if (frequency <= 0)
            return double.NaN;
        return (double)(qpcTicks - originTicks) * 1000.0 / frequency;
    }

    /// <summary>
    /// The time of a frame on the live clock: its <c>LastPresentTime</c> converted with <see cref="QpcToMs"/>, or <paramref name="acquiredAtMs"/> (the clock read right after the
    /// frame was acquired) when the present time is 0 (none), cannot be converted, or is not plausible (later than the acquisition by more than <see cref="MaxAheadMs"/>
    /// or earlier by more than <see cref="MaxBehindMs"/>).
    /// </summary>
    public static double FrameMs(long lastPresentQpc, long originTicks, long frequency, double acquiredAtMs)
    {
        if (lastPresentQpc == 0)
            return acquiredAtMs;
        var ms = QpcToMs(lastPresentQpc, originTicks, frequency);
        if (double.IsNaN(ms) || ms > acquiredAtMs + MaxAheadMs || ms < acquiredAtMs - MaxBehindMs)
            return acquiredAtMs;
        return ms;
    }
}
