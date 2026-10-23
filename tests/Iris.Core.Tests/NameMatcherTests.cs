using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class NameMatcherTests
{
    [Theory]
    [InlineData("ALPHA_ONE", "ALPHA_ONE", true)]
    [InlineData("ALPHA_ONE", "alpha_one", true)]     // case does not matter
    [InlineData("ALPHA_0NE", "ALPHA_ONE", true)]     // 0 <-> O
    [InlineData("ALPHA_ONE", "ALPHA_0NE", true)]
    [InlineData("L1GHT", "LIGHT", true)]             // 1 <-> I
    [InlineData("LIGHT", "L1GHT", true)]
    [InlineData("B0B1", "BOBI", true)]
    [InlineData("ALPHA_ONE", "ALPHA_0N3", false)]    // 3 is not a look-alike of E
    [InlineData("ABCD", "ABXD", false)]              // no other edit tolerance: K/N/X, D/Q/O, 5/S, 8/B, 6/G, 2/Z, L/1/7 are different letters
    [InlineData("QUIET", "0UIET", false)]
    [InlineData("DUCK", "0UCK", false)]
    [InlineData("SAND", "5AND", false)]
    [InlineData("BLUE", "8LUE", false)]
    [InlineData("LION", "1ION", false)]
    [InlineData("LION", "7ION", false)]
    [InlineData("ALPHA", "ALPHAS", false)]           // a missing or an extra letter is not tolerated
    public void Matches_ToleratesOnlyTheAccuracyLookAlikes(string a, string b, bool expected)
    {
        Assert.Equal(expected, NameMatcher.Matches(a, b));
        Assert.Equal(expected, NameMatcher.Matches(b, a));
    }

    [Fact]
    public void BestMatch_FindsTheNameOfTheVocabulary()
    {
        var vocabulary = new[] { "ALPHA_ONE", "BRAVO-TWO", "CHARLIE3" };

        Assert.Equal("ALPHA_ONE", NameMatcher.BestMatch("ALPHA_ONE", vocabulary));
        Assert.Equal("ALPHA_ONE", NameMatcher.BestMatch("ALPHA_0NE", vocabulary));
        Assert.Equal("BRAVO-TWO", NameMatcher.BestMatch("BRAV0-TW0", vocabulary));
        Assert.Equal("CHARLIE3", NameMatcher.BestMatch("charlie3", vocabulary));
    }

    [Fact]
    public void BestMatch_AnExactMatchBeatsALookAlikeMatch()
    {
        Assert.Equal("B0B1", NameMatcher.BestMatch("B0B1", new[] { "BOBI", "B0B1" }));
        Assert.Equal("BOBI", NameMatcher.BestMatch("B0B1", new[] { "BOBI" }));
    }

    [Fact]
    public void BestMatch_ReturnsNullWhenNothingMatches()
    {
        var vocabulary = new[] { "ALPHA_ONE", "BRAVO-TWO" };

        Assert.Null(NameMatcher.BestMatch("ALPHA_ON", vocabulary));        // one letter short: no edit tolerance
        Assert.Null(NameMatcher.BestMatch("ALPHA_ONF", vocabulary));
        Assert.Null(NameMatcher.BestMatch("ALPHA_ONE", Array.Empty<string>()));
        Assert.Null(NameMatcher.BestMatch("", vocabulary));
    }
}
