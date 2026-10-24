using Iris.Core.Live;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The pure parts of the event-driven tracking: the cap decision, the QPC-to-clock conversion and the game frame rate of the minute line.</summary>
public class FrameTimingTests
{
    // ----- TrackingSchedule -----

    private static int CountTracked(TrackingSchedule schedule, int gameFps, int cap, int seconds)
    {
        var count = 0;
        for (var i = 0; i < gameFps * seconds; i++)
        {
            if (schedule.Offer(i * 1000.0 / gameFps, cap))
                count++;
        }

        return count;
    }

    [Fact]
    public void Cap_TheFirstFrameIsAlwaysTracked()
    {
        Assert.True(new TrackingSchedule().Offer(1234.0, 60));
        Assert.True(new TrackingSchedule().Offer(0.0, 30));
        Assert.True(new TrackingSchedule().Offer(5.0, 0));
    }

    [Fact]
    public void Cap_MaxTracksEveryNewFrame()
    {
        var schedule = new TrackingSchedule();
        Assert.True(schedule.Offer(100.0, 0));
        Assert.True(schedule.Offer(101.0, 0));
        Assert.True(schedule.Offer(101.0, 0));
        Assert.True(schedule.Offer(101.1, -1));
    }

    [Theory]
    [InlineData(144, 60)]
    [InlineData(144, 30)]
    [InlineData(240, 90)]
    [InlineData(100, 60)]
    [InlineData(75, 30)]
    public void Cap_IsMetOnAverage(int gameFps, int cap)
    {
        var tracked = CountTracked(new TrackingSchedule(), gameFps, cap, 10);
        // within 2 % of cap x seconds (the first frame is tracked at once)
        Assert.InRange(tracked, cap * 10 * 0.98, cap * 10 * 1.02 + 1);
    }

    [Fact]
    public void Cap_AGameAtTheCapIsTrackedEveryFrame_ThanksToTheSlack()
    {
        var schedule = new TrackingSchedule();
        Assert.Equal(600, CountTracked(schedule, 60, 60, 10));

        // frame times that jitter by +-0.8 ms around 16.67 ms
        var jittered = new TrackingSchedule();
        var count = 0;
        for (var i = 0; i < 600; i++)
        {
            var jitter = (i % 2 == 0 ? 0.8 : -0.8);
            if (jittered.Offer(i * 1000.0 / 60.0 + jitter, 60))
                count++;
        }

        Assert.Equal(600, count);
    }

    [Fact]
    public void Cap_ASlowGameIsTrackedAtItsOwnRate()
    {
        Assert.Equal(240, CountTracked(new TrackingSchedule(), 24, 90, 10));
    }

    [Fact]
    public void Cap_ALongGapDoesNotBuyABurst()
    {
        var schedule = new TrackingSchedule();
        Assert.True(schedule.Offer(0.0, 60));
        Assert.True(schedule.Offer(5000.0, 60)); // after a 5 s stall
        // the schedule is at most one period behind the frame: one second of a 144 fps game after the stall is tracked at about the cap, not 144 times
        var count = 0;
        for (var i = 1; i <= 144; i++)
        {
            if (schedule.Offer(5000.0 + i * 1000.0 / 144.0, 60))
                count++;
        }

        Assert.InRange(count, 58, 63);
    }

    [Fact]
    public void Cap_ResetAndMaxForgetTheSchedule()
    {
        var schedule = new TrackingSchedule();
        Assert.True(schedule.Offer(0.0, 60));
        Assert.False(schedule.Offer(2.0, 60));
        schedule.Reset();
        Assert.True(schedule.Offer(3.0, 60));
        Assert.False(schedule.Offer(4.0, 60));
        Assert.True(schedule.Offer(5.0, 0)); // Max
        Assert.True(double.IsNaN(schedule.NextAllowedMs));
    }

    [Theory]
    [InlineData(30, 33.33)]
    [InlineData(60, 16.67)]
    [InlineData(90, 11.11)]
    public void Cap_PeriodIsOneOverTheCap(int cap, double expectedMs)
    {
        Assert.Equal(expectedMs, TrackingSchedule.PeriodMs(cap), 2);
        Assert.Equal(0.0, TrackingSchedule.PeriodMs(0));
    }

    // ----- FrameClock -----

    [Fact]
    public void QpcToMs_UsesTheFrequencyAndTheOrigin()
    {
        // a 10 MHz counter (Windows' usual QPC frequency): 10 000 ticks are 1 ms
        Assert.Equal(0.0, FrameClock.QpcToMs(5_000_000, 5_000_000, 10_000_000), 9);
        Assert.Equal(1.0, FrameClock.QpcToMs(5_010_000, 5_000_000, 10_000_000), 9);
        Assert.Equal(1500.0, FrameClock.QpcToMs(5_000_000 + 15_000_000, 5_000_000, 10_000_000), 6);
        Assert.Equal(-2.5, FrameClock.QpcToMs(4_975_000, 5_000_000, 10_000_000), 9); // before the origin
        // a 1 GHz counter: 16 667 000 ticks are 16.667 ms
        Assert.Equal(16.667, FrameClock.QpcToMs(1_016_667_000, 1_000_000_000, 1_000_000_000), 6);
    }

    [Fact]
    public void QpcToMs_HasNoPrecisionLossForLargeCounters()
    {
        // 30 days of uptime at 10 MHz is 2.6e13 ticks: a 6.9 ms frame step must still come out as 6.9 ms
        const long origin = 25_920_000_000_000L;
        const long frequency = 10_000_000L;
        var a = FrameClock.QpcToMs(origin + 123_456_789L, origin, frequency);
        var b = FrameClock.QpcToMs(origin + 123_456_789L + 69_444L, origin, frequency);

        Assert.Equal(6.9444, b - a, 4);
    }

    [Fact]
    public void QpcToMs_NaNWithoutAFrequency()
    {
        Assert.True(double.IsNaN(FrameClock.QpcToMs(100, 0, 0)));
        Assert.True(double.IsNaN(FrameClock.QpcToMs(100, 0, -1)));
    }

    [Fact]
    public void FrameMs_ConvertsThePresentTime()
    {
        // origin 1 000 000 ticks, 10 MHz: tick 1 520 000 is 52 ms on the clock ((1 520 000 - 1 000 000) / 10 000); the frame was acquired at 53.4 ms
        var ms = FrameClock.FrameMs(1_520_000, 1_000_000, 10_000_000, acquiredAtMs: 53.4);

        Assert.Equal(52.0, ms, 6);
    }

    [Fact]
    public void FrameMs_ZeroPresentTimeUsesTheClockAtAcquire()
    {
        Assert.Equal(777.7, FrameClock.FrameMs(0, 1_000_000, 10_000_000, 777.7));
    }

    [Fact]
    public void FrameMs_ANotPlausiblePresentTimeUsesTheClockAtAcquire()
    {
        const long origin = 1_000_000;
        const long frequency = 10_000_000;
        var acquired = 500.0; // tick = origin + 5 000 000
        var nowTicks = origin + 5_000_000;

        // in the future by 5 ms (> the 2 ms allowed): the frame cannot be newer than its acquisition
        Assert.Equal(acquired, FrameClock.FrameMs(nowTicks + 50_000, origin, frequency, acquired));
        // 1.5 s old: not the time of this frame
        Assert.Equal(acquired, FrameClock.FrameMs(nowTicks - 15_000_000, origin, frequency, acquired));
        // a counter that is not the stopwatch's at all
        Assert.Equal(acquired, FrameClock.FrameMs(long.MaxValue / 4, origin, frequency, acquired));
        // a frequency of 0
        Assert.Equal(acquired, FrameClock.FrameMs(nowTicks, origin, 0, acquired));
    }

    [Fact]
    public void FrameMs_APresentTimeJustBeforeTheAcquireIsKept()
    {
        const long origin = 0;
        const long frequency = 10_000_000;
        var acquired = 500.0;

        // presented 3.3 ms before the acquire (a game frame the compositor picked up): that is the time of the picture
        Assert.Equal(496.7, FrameClock.FrameMs(4_967_000, origin, frequency, acquired), 6);
        // 1 ms "ahead" is within the tolerance (clock read order, rounding)
        Assert.Equal(501.0, FrameClock.FrameMs(5_010_000, origin, frequency, acquired), 6);
    }

    // ----- the game frame rate of the minute line -----

    [Fact]
    public void FrameRate_IsFramesMinusOneOverTheSpan()
    {
        Assert.Equal(0.0, LiveStats.FrameRate(0, 0, 0));
        Assert.Equal(0.0, LiveStats.FrameRate(1, 100, 100));
        Assert.Equal(0.0, LiveStats.FrameRate(5, 100, 100)); // no span
        Assert.Equal(100.0, LiveStats.FrameRate(11, 0, 100), 9); // 10 intervals in 100 ms
    }

    [Fact]
    public void Stats_TheSummaryHasTheGameFrameRateAndTheTrackingRate()
    {
        var stats = new LiveStats(0);
        // a 144 fps game for 2 s: 289 new frames (288 intervals), every third of the first 288 tracked (96 frames in 2 s)
        for (var i = 0; i <= 288; i++)
        {
            stats.AddGameFrame(i * 1000.0 / 144.0);
            if (i % 3 == 0 && i < 288)
                stats.AddTrackedFrame();
        }

        var line = stats.TakeSummary(2_000, 3, trackingCapFps: 60);

        Assert.Contains("game frames ~144/s", line);
        Assert.Contains("tracked ~48/s (cap 60)", line);
    }

    [Fact]
    public void Stats_TheSummaryWithoutACapSaysSo_AndStartsANewFrameWindow()
    {
        var stats = new LiveStats(0);
        stats.AddGameFrame(0);
        stats.AddGameFrame(10);
        stats.AddTrackedFrame();
        stats.AddTrackedFrame();

        var first = stats.TakeSummary(1_000, 0);
        var second = stats.TakeSummary(2_000, 0);

        Assert.Contains("game frames ~100/s, tracked ~2/s (no cap)", first);
        Assert.Contains("game frames ~0/s, tracked ~0/s (no cap)", second);
    }
}
