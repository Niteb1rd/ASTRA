namespace Iris.Core.Live;

/// <summary>
/// The cap of the tracking, as a pure decision (no clock, no threads): the capture loop wakes for every NEW frame of the game and asks whether this frame is to be tracked.
/// The cap is met ON AVERAGE by a running schedule: a frame is tracked when it is not earlier than the next allowed time (less a small slack for jitter, see <see cref="SlackMs"/>), and the next
/// allowed time then moves one period on from where it was (so a frame that came a little early or late does not shift the whole rhythm), but never more than one period
/// behind the frame (a long gap does not buy a burst). With no cap (Max) every new frame is tracked.
/// </summary>
public sealed class TrackingSchedule
{
    /// <summary>
    /// A frame this much short of the next allowed time still counts: at least <see cref="SlackMs"/>, or <see cref="SlackFraction"/> of the period when that is more
    /// (2.5 ms at a cap of 60). Frame times jitter by a millisecond or two, and a 60 fps game at a cap of 60 must still be tracked on every frame; the running schedule
    /// keeps the average at the cap whatever the slack.
    /// </summary>
    public const double SlackMs = 1.0;

    /// <summary>The slack as a share of the period (see <see cref="SlackMs"/>).</summary>
    public const double SlackFraction = 0.15;

    private double _nextAllowed = double.NaN;

    /// <summary>The time from which the next frame may be tracked (NaN: any, none tracked yet or no cap).</summary>
    public double NextAllowedMs => _nextAllowed;

    /// <summary>Forget the schedule (a new run): the next frame is tracked.</summary>
    public void Reset() => _nextAllowed = double.NaN;

    /// <summary>The period of a cap in milliseconds (0 for no cap, or a cap of 0 or less).</summary>
    public static double PeriodMs(int capFps) => capFps <= 0 ? 0.0 : 1000.0 / capFps;

    /// <summary>Whether the new frame at <paramref name="frameMs"/> is to be tracked under the cap (<paramref name="capFps"/> frames a second; 0 or less means Max: every new frame). Call once per new frame.</summary>
    public bool Offer(double frameMs, int capFps)
    {
        if (capFps <= 0)
        {
            _nextAllowed = double.NaN;
            return true;
        }

        var period = PeriodMs(capFps);
        if (double.IsNaN(_nextAllowed))
        {
            _nextAllowed = frameMs + period;
            return true;
        }

        if (frameMs < _nextAllowed - Math.Max(SlackMs, period * SlackFraction))
            return false;
        _nextAllowed = Math.Max(_nextAllowed + period, frameMs - period);
        return true;
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
