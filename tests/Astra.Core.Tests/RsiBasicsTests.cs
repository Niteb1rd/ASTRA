using Astra.Core.Rsi;
using Xunit;

namespace Astra.Core.Tests;

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
    private static readonly TimeSpan Three = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task FirstRequest_DoesNotWait_FollowingOnesWaitThreeSeconds()
    {
        var clock = new FakeClock();
        var waits = new List<TimeSpan>();
        var limiter = new RsiRateLimiter(clock, (wait, ct) =>
        {
            waits.Add(wait);
            clock.Advance(wait);   // the fake delay "passes" the time
            return Task.CompletedTask;
        });

        await limiter.WaitForTurnAsync();
        await limiter.WaitForTurnAsync();
        await limiter.WaitForTurnAsync();

        Assert.Equal(new[] { Three, Three }, waits);
    }

    [Fact]
    public async Task RequestsThatArrivedTogether_AreSpacedThreeSecondsApart_InOrder()
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

        // first goes at once; second 3 s later; third 6 s later
        Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) }, waits);
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
        clock.Advance(TimeSpan.FromSeconds(10));
        await limiter.WaitForTurnAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await limiter.WaitForTurnAsync();   // only 2 s since the previous one: must wait 1 s

        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, waits);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitForTurnAsync(cts.Token)); // reserved +3 s, then cancelled
        await limiter.WaitForTurnAsync();                         // must get the cancelled caller's slot: +3 s, not +6 s

        Assert.Equal(new[] { Three, Three }, waits);
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
        var cancelled = limiter.WaitForTurnAsync();               // +3 s, still waiting
        await limiter.WaitForTurnAsync();                         // +6 s, behind it
        firstWaiter.SetCanceled();                                // the +3 s waiter is cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // Its slot was NOT given back (a later caller sits behind it): the next caller is at +9 s.
        await limiter.WaitForTurnAsync();
        Assert.Equal(new[] { Three, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9) }, waits);
    }

    [Fact]
    public void DefaultInterval_IsThreeSeconds() => Assert.Equal(Three, RsiRateLimiter.DefaultInterval);
}
