using Iris.Core.Rsi;
using Xunit;

namespace Iris.Core.Tests;

public class RsiNamesTests
{
    [Theory]
    [InlineData("Nitebird")]
    [InlineData("Love2Hate")]
    [InlineData("abcd")]
    [InlineData("a-_9")]
    [InlineData("Under_score-Dash")]
    public void ValidHandles_Accepted(string handle) => Assert.True(RsiNames.IsValidHandle(handle));

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("a?b=c")]
    [InlineData("a#b")]
    [InlineData("tab\there")]
    [InlineData("trailing-newline\n")]
    [InlineData("Ünicode")]
    public void InvalidHandles_Rejected(string handle) => Assert.False(RsiNames.IsValidHandle(handle));

    [Fact]
    public void Handle_LengthLimitsAre4To25()
    {
        Assert.Equal(4, RsiNames.MinHandleLength);
        Assert.Equal(25, RsiNames.MaxHandleLength);
        Assert.False(RsiNames.IsValidHandle(new string('a', 3)));
        Assert.True(RsiNames.IsValidHandle(new string('a', 4)));
        Assert.True(RsiNames.IsValidHandle(new string('a', 25)));
        Assert.False(RsiNames.IsValidHandle(new string('a', 26)));
        Assert.False(RsiNames.IsValidHandle(null));
    }

    [Fact]
    public void Sid_LengthLimitIs20_AndCharactersChecked()
    {
        Assert.True(RsiNames.IsValidSid("J0K3R5"));
        Assert.True(RsiNames.IsValidSid(new string('A', 20)));
        Assert.False(RsiNames.IsValidSid(new string('A', 21)));
        Assert.False(RsiNames.IsValidSid("bad sid"));
        Assert.False(RsiNames.IsValidSid("SID\n"));
        Assert.False(RsiNames.IsValidSid(""));
        Assert.False(RsiNames.IsValidSid(null));
    }
}

public class RsiUrlsTests
{
    [Fact]
    public void PlayerAndOrgUrls_AreBuiltFromValidatedNames()
    {
        Assert.Equal("https://robertsspaceindustries.com/en/citizens/Nitebird/organizations", RsiUrls.PlayerOrgs("Nitebird"));
        Assert.Equal("https://robertsspaceindustries.com/en/orgs/J0K3R5", RsiUrls.OrgPage("J0K3R5"));
        Assert.Equal("https://robertsspaceindustries.com/api/orgs/getOrgMembers", RsiUrls.OrgMembersApi);
    }

    [Fact]
    public void UrlBuilders_RefuseInvalidNames()
    {
        Assert.Throws<ArgumentException>(() => RsiUrls.PlayerOrgs("bad/name"));
        Assert.Throws<ArgumentException>(() => RsiUrls.OrgPage("bad name"));
    }

    [Theory]
    [InlineData("https://robertsspaceindustries.com/en/orgs/X", true)]
    [InlineData("https://ROBERTSSPACEINDUSTRIES.com/en/orgs/X", true)]
    [InlineData("http://robertsspaceindustries.com/en/orgs/X", false)]
    [InlineData("https://www.robertsspaceindustries.com/en/orgs/X", false)]
    [InlineData("https://example.com/en/orgs/X", false)]
    [InlineData("https://robertsspaceindustries.com.evil.example/x", false)]
    [InlineData("https://robertsspaceindustries.com@evil.example/x", false)]
    [InlineData("https://robertsspaceindustries.com:8443/x", false)]
    public void OnlyRsiOverHttpsIsAllowed(string url, bool allowed) =>
        Assert.Equal(allowed, RsiUrls.IsAllowed(new Uri(url)));
}

public class RsiRateLimiterTests
{
    private static readonly TimeSpan One = TimeSpan.FromSeconds(1);

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    /// <summary>A limiter whose waits "pass" on the fake clock, like real sleeping would.</summary>
    private static RsiRateLimiter Realistic(FakeClock clock, List<TimeSpan> waits) =>
        new(clock, (wait, ct) =>
        {
            waits.Add(wait);
            clock.Advance(wait);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task FirstRequest_DoesNotWait_FollowingOnesWaitOneSecond()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = Realistic(clock, waits);

        await limiter.WaitForTurnAsync();
        await limiter.WaitForTurnAsync();
        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { One, One }, waits);
    }

    [Fact]
    public async Task RequestsThatArrivedTogether_AreSpacedOneSecondApart_InOrder()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = new RsiRateLimiter(clock, (wait, ct) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;   // the clock does not move: all three callers see the same "now"
        });

        var a = limiter.WaitForTurnAsync();
        var b = limiter.WaitForTurnAsync();
        var c = limiter.WaitForTurnAsync();
        await Task.WhenAll(a, b, c);

        // first goes at once; second 1 s later; third 2 s later
        Assert.Equal(new[] { Seconds(1), Seconds(2) }, waits);
    }

    [Fact]
    public async Task NoWait_WhenEnoughTimeHasPassedAnyway()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = new RsiRateLimiter(clock, (wait, ct) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });

        await limiter.WaitForTurnAsync();
        clock.Advance(Seconds(10));
        await limiter.WaitForTurnAsync();
        clock.Advance(Seconds(0.25));
        await limiter.WaitForTurnAsync();   // only 0.25 s since the previous one: must wait the other 0.75 s

        Assert.Equal(new[] { Seconds(0.75) }, waits);
    }

    private static RsiRateLimiter CancellableLimiter(FakeClock clock, List<TimeSpan> waits) =>
        new(clock, (wait, ct) =>
        {
            waits.Add(wait);
            return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        });

    [Fact]
    public async Task CancelledWait_GivesItsSlotBack_WhenNobodyReservedAfterIt()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = CancellableLimiter(clock, waits);

        await limiter.WaitForTurnAsync();                         // takes the slot at "now"; no wait
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitForTurnAsync(cts.Token)); // reserved +1 s, then cancelled
        await limiter.WaitForTurnAsync();                         // must get the cancelled caller's slot: +1 s, not +2 s

        Assert.Equal(new[] { One, One }, waits);
    }

    [Fact]
    public async Task CancelledWait_KeepsItsSlot_WhenALaterCallerReservedAfterIt()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var firstWaiter = new TaskCompletionSource();
        var limiter = new RsiRateLimiter(clock, (wait, ct) =>
        {
            waits.Add(wait);
            return waits.Count == 1 ? firstWaiter.Task : Task.CompletedTask; // only the first waiter stays pending
        });

        await limiter.WaitForTurnAsync();                         // no wait
        var cancelled = limiter.WaitForTurnAsync();               // +1 s, still waiting
        await limiter.WaitForTurnAsync();                         // +2 s, behind it
        firstWaiter.SetCanceled();                                // the +1 s waiter is cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // Its slot was NOT given back (a later caller sits behind it): the next caller is at +3 s.
        await limiter.WaitForTurnAsync();
        Assert.Equal(new[] { Seconds(1), Seconds(2), Seconds(3) }, waits);
    }

    [Fact]
    public void DefaultInterval_IsOneSecond() => Assert.Equal(One, RsiRateLimiter.DefaultInterval);

    // ------------------------------------------------------------------ backoff

    [Fact]
    public async Task EachBadAnswer_DoublesTheGap_UpTo30Seconds_AndTheNextRequestWaitsThatLong()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = Realistic(clock, waits);
        await limiter.WaitForTurnAsync();

        var gaps = new List<double>();
        foreach (var _ in new[] { 1, 2, 3, 4, 5, 6, 7 })
        {
            limiter.ReportThrottled();
            gaps.Add(limiter.CurrentGap.TotalSeconds);
            await limiter.WaitForTurnAsync();
        }

        Assert.Equal(new[] { 2.0, 4.0, 8.0, 16.0, 30.0, 30.0, 30.0 }, gaps);
        Assert.Equal(new[] { Seconds(2), Seconds(4), Seconds(8), Seconds(16), Seconds(30), Seconds(30), Seconds(30) }, waits);
    }

    [Fact]
    public void EachGoodAnswer_HalvesTheGap_BackDownToTheNormalInterval()
    {
        var clock = new FakeClock();
        var limiter = Realistic(clock, new List<TimeSpan>());
        for (int i = 0; i < 4; i++)
            limiter.ReportThrottled();   // 1 -> 16
        Assert.Equal(Seconds(16), limiter.CurrentGap);

        var seen = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            limiter.ReportSuccess();
            seen.Add(limiter.CurrentGap.TotalSeconds);
        }

        Assert.Equal(new[] { 8.0, 4.0, 2.0, 1.0, 1.0, 1.0 }, seen);
    }

    [Fact]
    public void ReportedChanges_SayWhatChanged()
    {
        var limiter = new RsiRateLimiter(new FakeClock());

        var slower = limiter.ReportThrottled();
        Assert.True(slower.Changed);
        Assert.Equal(One, slower.Before);
        Assert.Equal(Seconds(2), slower.After);
        Assert.Equal(Seconds(2), slower.NextDelay);

        var faster = limiter.ReportSuccess();
        Assert.True(faster.Changed);
        Assert.Equal(Seconds(2), faster.Before);
        Assert.Equal(One, faster.After);

        var same = limiter.ReportSuccess();   // already at the normal gap
        Assert.False(same.Changed);
    }

    [Fact]
    public async Task ASuccessForARequestSentBeforeTheLatestBadAnswer_IsIgnored()
    {
        var clock = new FakeClock();
        var limiter = Realistic(clock, new List<TimeSpan>());
        var early = await limiter.WaitForTurnAsync();      // sent before the bad answer below
        var other = await limiter.WaitForTurnAsync();      // also sent before it

        limiter.ReportThrottled();                         // gap 2 s, a new generation
        var stale = limiter.ReportSuccess(early);          // its good answer arrives late: says nothing about now
        Assert.False(stale.Changed);
        Assert.Equal(Seconds(2), limiter.CurrentGap);
        Assert.Equal(Seconds(2), limiter.ReportSuccess(other).After);

        var fresh = await limiter.WaitForTurnAsync();      // sent after the bad answer
        var good = limiter.ReportSuccess(fresh);
        Assert.True(good.Changed);
        Assert.Equal(One, limiter.CurrentGap);
    }

    [Fact]
    public async Task ASuccessWithoutATurn_AlwaysCounts()
    {
        var limiter = new RsiRateLimiter(new FakeClock());
        await limiter.WaitForTurnAsync();
        limiter.ReportThrottled();

        Assert.Equal(One, limiter.ReportSuccess().After);
    }

    [Fact]
    public async Task RetryAfter_WinsWhenLongerThanTheBackoff_AndIsCappedAt120Seconds()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = Realistic(clock, waits);
        await limiter.WaitForTurnAsync();

        var change = limiter.ReportThrottled(Seconds(10));     // backoff would be 2 s
        await limiter.WaitForTurnAsync();
        Assert.Equal(Seconds(10), change.NextDelay);
        Assert.Equal(Seconds(2), change.After);                // the gap itself only doubled

        limiter.ReportThrottled(Seconds(1000));                // way too long: capped
        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { Seconds(10), Seconds(120) }, waits);
    }

    [Fact]
    public async Task RetryAfter_ShorterThanTheBackoff_IsIgnored()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = Realistic(clock, waits);
        await limiter.WaitForTurnAsync();
        limiter.ReportThrottled();
        await limiter.WaitForTurnAsync();   // 2 s
        waits.Clear();

        limiter.ReportThrottled(Seconds(1));   // gap is 4 s now, Retry-After only 1 s
        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { Seconds(4) }, waits);
    }

    [Fact]
    public async Task TheBackoffWaitCountsFromTheBadAnswer_NotFromTheLastSend()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = Realistic(clock, waits);
        await limiter.WaitForTurnAsync();

        clock.Advance(Seconds(0.5));      // the request took half a second before RSI said 429
        limiter.ReportThrottled();        // gap 2 s
        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { Seconds(2) }, waits);
    }

    [Fact]
    public async Task AfterRecovery_TheNextRequestWaitsOnlyTheNewSmallerGap()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = Realistic(clock, waits);
        await limiter.WaitForTurnAsync();
        limiter.ReportThrottled();
        await limiter.WaitForTurnAsync();   // waits 2 s; gap is 2 s
        limiter.ReportSuccess();            // gap back to 1 s
        waits.Clear();

        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { One }, waits);
    }

    [Fact]
    public async Task ARequestAlreadyWaitingForItsSlot_AlsoWaitsOutABackoffThatArrivesMeanwhile()
    {
        var clock = new FakeClock();
        var start = clock.UtcNow;
        var waits = new List<TimeSpan>();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var sleeps = 0;
        var limiter = new RsiRateLimiter(clock, async (wait, ct) =>
        {
            waits.Add(wait);
            if (++sleeps == 1)
            {
                started.SetResult();
                await release.Task;   // the first sleeper stays asleep until the test lets it go
            }
            clock.Advance(wait);
        });

        await limiter.WaitForTurnAsync();                 // first caller: no wait
        var waiting = limiter.WaitForTurnAsync();         // second caller: reserved +1 s, now asleep
        await started.Task;

        limiter.ReportThrottled();                        // a bad answer arrives meanwhile: nothing may go out for 2 s from now
        release.SetResult();
        await waiting;

        Assert.Equal(new[] { One, One }, waits);          // its 1 s sleep, then 1 more second until the backoff is over
        Assert.Equal(Seconds(2), clock.UtcNow - start);
    }
}
