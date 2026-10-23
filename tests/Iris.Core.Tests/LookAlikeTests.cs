using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class LookAlikeTests
{
    [Fact]
    public void AccuracyList_IsOnlyZeroO_And_OneI()
    {
        Assert.Equal(new[] { "0O", "1I" }, LookAlikes.AccuracyGroups);
        for (var c = (char)32; c < 127; c++)
        {
            var expected = char.ToUpperInvariant(c) switch { 'O' => '0', 'I' => '1', var other => other };
            Assert.Equal(expected, LookAlikes.FoldNameChar(c));
        }
    }

    [Fact]
    public void EfficiencyList_IsTheSixGroups()
    {
        Assert.Equal(new[] { "0OQD", "1I7", "5S", "8B", "6G", "2Z" }, LookAlikes.EfficiencyGroups);
        Assert.Equal("OQDISBGZ", LookAlikes.EfficiencyLetters);
    }

    [Theory]
    [InlineData('0', '0')]
    [InlineData('O', '0')]
    [InlineData('Q', '0')]
    [InlineData('D', '0')]
    [InlineData('1', '1')]
    [InlineData('I', '1')]
    [InlineData('7', '7')]   // a digit stands for itself, even though 7 is in the group of 1 and I
    [InlineData('S', '5')]
    [InlineData('B', '8')]
    [InlineData('G', '6')]
    [InlineData('Z', '2')]
    [InlineData('9', '9')]
    public void EfficiencyDigit_MapsLookAlikesToDigits(char c, char digit) => Assert.Equal(digit, LookAlikes.EfficiencyDigit(c));

    [Theory]
    [InlineData('L')]
    [InlineData('K')]
    [InlineData('N')]
    [InlineData('X')]
    [InlineData('A')]
    [InlineData('.')]
    [InlineData('k')]
    [InlineData('o')] // lower case letters are not mapped: the distance read only makes lower case k and m
    public void EfficiencyDigit_IsNullForOtherCharacters(char c) => Assert.Null(LookAlikes.EfficiencyDigit(c));

    [Fact]
    public void FoldName_And_NamesEqual()
    {
        Assert.Equal("B0B1", LookAlikes.FoldName("BOBI"));
        Assert.Equal("B0B1", LookAlikes.FoldName("bob1"));
        Assert.Equal(string.Empty, LookAlikes.FoldName(null));
        Assert.True(LookAlikes.NamesEqual("Nitebird", "N1TEB1RD"));
        Assert.False(LookAlikes.NamesEqual("Nitebird", "N1TEB1R0"));  // that is a D read as 0
        Assert.False(LookAlikes.NamesEqual(null, "x"));
        Assert.False(LookAlikes.NamesEqual("x", null));
    }

    [Fact]
    public void NameVariants_SwapOnlyTheAccuracyLookAlikes()
    {
        Assert.Empty(LookAlikes.NameVariants("ABCD"));
        Assert.Equal(new[] { "AB0D" }, LookAlikes.NameVariants("ABOD"));

        var variants = LookAlikes.NameVariants("O1");
        Assert.Equal(new[] { "01", "OI", "0I" }, variants);   // one swap first, then both
        Assert.DoesNotContain("O1", variants);
    }

    [Fact]
    public void NameVariants_KeepTheCaseAndAreLimited()
    {
        Assert.Equal(new[] { "n0te" }, LookAlikes.NameVariants("note"));
        Assert.Equal(new[] { "L1ght" }, LookAlikes.NameVariants("Light"));
        var many = LookAlikes.NameVariants("OOOOOOOOOO"); // only the first 5 positions swap: 31 variants
        Assert.Equal(31, many.Count);
        Assert.Equal(31, many.Distinct().Count());
        Assert.Equal(5, LookAlikes.NameVariants("OOOOOOOOOO", 5).Count);
        Assert.All(many, v => Assert.True(LookAlikes.NamesEqual(v, "OOOOOOOOOO")));
    }
}
