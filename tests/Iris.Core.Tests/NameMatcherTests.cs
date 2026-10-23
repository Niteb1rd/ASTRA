using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class NameMatcherTests
{
    [Fact]
    public void ConfusableCharacters_CostLessThanOtherSubstitutions()
    {
        Assert.Equal(0.0, NameMatcher.SubstitutionCost('A', 'A'));
        Assert.Equal(0.35, NameMatcher.SubstitutionCost('0', 'O'));
        Assert.Equal(0.35, NameMatcher.SubstitutionCost('D', 'Q'));
        Assert.Equal(0.35, NameMatcher.SubstitutionCost('7', 'L'));
        Assert.Equal(0.35, NameMatcher.SubstitutionCost('X', 'K'));
        Assert.Equal(1.0, NameMatcher.SubstitutionCost('0', '1'));
        Assert.Equal(1.0, NameMatcher.SubstitutionCost('A', 'B'));
    }

    [Theory]
    [InlineData("ABC", "ABC", 0.0)]
    [InlineData("ABC", "ABCD", 1.0)]
    [InlineData("", "ABC", 3.0)]
    [InlineData("ABC", "", 3.0)]
    [InlineData("B0B", "BOB", 0.35)]
    [InlineData("KITTEN", "SITTING", 3.0)]
    public void WeightedDistance_IsTheEditDistanceWithCheapConfusions(string a, string b, double expected)
    {
        Assert.Equal(expected, NameMatcher.WeightedDistance(a, b), 9);
        Assert.Equal(expected, NameMatcher.WeightedDistance(b, a), 9);
    }

    [Fact]
    public void BestMatch_FindsTheLikelyName()
    {
        var vocabulary = new[] { "ALPHA_ONE", "BRAVO-TWO", "CHARLIE3" };

        Assert.Equal("ALPHA_ONE", NameMatcher.BestMatch("ALPHA_ONE", vocabulary));
        Assert.Equal("ALPHA_ONE", NameMatcher.BestMatch("ALPHA_0NE", vocabulary));   // confusable: 0.35
        Assert.Equal("BRAVO-TWO", NameMatcher.BestMatch("BRAV0-TW0", vocabulary));   // 0.7 <= max(1, 1.8)
        Assert.Equal("CHARLIE3", NameMatcher.BestMatch("CHARLIE", vocabulary));      // one missing letter, limit max(1, 1.6)
    }

    [Fact]
    public void BestMatch_RefusesWhatIsTooFarOffOrTooDifferentInLength()
    {
        var vocabulary = new[] { "ALPHA_ONE", "BRAVO-TWO" };

        Assert.Null(NameMatcher.BestMatch("ZZZZZ_ZZZ", vocabulary));  // same length, far off
        Assert.Null(NameMatcher.BestMatch("ALPHA", vocabulary));      // more than 2 characters shorter: not even compared
        Assert.Null(NameMatcher.BestMatch("ALPHA_ONE", Array.Empty<string>()));
    }

    [Fact]
    public void BestMatch_ShortNamesAllowOneEdit()
    {
        Assert.Equal("ABCD", NameMatcher.BestMatch("ABXD", new[] { "ABCD" }));   // 1.0 <= max(1, 0.8)
        Assert.Null(NameMatcher.BestMatch("AXYD", new[] { "ABCD" }));            // 2.0 > 1
    }
}
