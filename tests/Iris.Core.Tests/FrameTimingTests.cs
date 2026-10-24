using Iris.Core.Live;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The pure parts of the event-driven tracking: the cap decision, the QPC-to-clock conversion and the game frame rate of the minute line.</summary>
public class FrameTimingTests
{
    // ----- TrackingCap -----

    [Fact]
    public void Cap_TheFirstFrameIsAlwaysTracked()
    {
        Assert.True(TrackingCap.ShouldTrack(1234.0, double.NaN, 60));
        Assert.True(TrackingCap.ShouldTrack(0.0, double.NaN, 30));
        Assert.True(TrackingCap.ShouldTrack(5.0, double.NaN, 0));
    }

    [Fact]
    public void Cap_MaxTracksEveryNewFrame()
    {
        // no cap other than the frames themselves: even frames 1 ms apart, even the same time twice
        Assert.True(TrackingCap.ShouldTrack(101.0, 100.0, 0));
        Assert.True(TrackingCap.ShouldTrack(100.0, 100.0, 0));
        Assert.True(TrackingCap.ShouldTrack(100.1, 100.0, -1));
    }

    [Fact]
    public void Cap_AGameFasterThanTheCapIsThinnedOut()
    {
        // a 144 fps game (6.94 ms frames) at a cap of 60 (period 16.67 ms, minus 1 ms of slack): every third frame
        var cap = 60;
        var last = 0.0;
        var tracked = new List<double> { last };
        for (var i = 1; i <= 144; i++)
        {
            var t = i * 1000.0 / 144.0;
            if (TrackingCap.ShouldTrack(t, last, cap))
            {
                last = t;
                tracked.Add(t);
            }
        }

        // frames at 6.94, 13.89 (< 15.67 since the last) are skipped, the third at 20.8 is tracked: 48 of 144 plus the first
        Assert.InRange(tracked.Count, 47, 50);
        for (var i = 1; i < tracked.Count; i++)
            Assert.True(tracked[i] - tracked[i - 1] >= TrackingCap.MinIntervalMs(cap));
    }

    [Fact]
    public void Cap_AGameAtTheCapIsTrackedEveryFrame_ThanksToTheSlack()
    {
        // 60 fps frames jitter around 16.67 ms: 16.0 ms after the last tracked one still counts (period minus the 1 ms slack)
        Assert.True(TrackingCap.ShouldTrack(116.0, 100.0, 60));
        Assert.True(TrackingCap.ShouldTrack(116.67, 100.0, 60));
        Assert.False(TrackingCap.ShouldTrack(115.0, 100.0, 60)); // 15 ms: too soon
    }

    [Theory]
    [InlineData(30, 32.33)]
    [InlineData(60, 15.67)]
    [InlineData(90, 10.11)]
    public void Cap_MinimumIntervalIsThePeriodMinusOneMillisecond(int cap, double expectedMs)
    {
        Assert.Equal(expectedMs, TrackingCap.MinIntervalMs(cap), 2);
        Assert.Equal(0.0, TrackingCap.MinIntervalMs(0));
        Assert.True(TrackingCap.ShouldTrack(1000.0 + expectedMs + 0.01, 1000.0, cap));
        Assert.False(TrackingCap.ShouldTrack(1000.0 + expectedMs - 0.05, 1000.0, cap));
    }

    [Fact]
    public void Cap_ASlowGameIsTrackedAtItsOwnRate()
    {
        // 24 fps film-like frames at a cap of 90: every frame (41.7 ms apart is more than the 10.1 ms minimum)
        var last = 0.0;
        var count = 0;
        for (var i = 1; i <= 24; i++)
        {
            var t = i * 1000.0 / 24.0;
            if (TrackingCap.ShouldTrack(t, last, 90))
            {
                last = t;
                count++;
            }
        }

        Assert.Equal(24, count);
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
