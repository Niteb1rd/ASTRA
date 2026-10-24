using Iris.Core.Detection;
using Xunit;

namespace Iris.Core.Tests;

public class LookAlikeTests
{
    [Fact]
    public void AccuracyList_IsEmpty()
    {
        Assert.Empty(LookAlikes.AccuracyGroups);
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
    public void NamesEqual_IgnoresOnlyTheCase()
    {
        Assert.True(LookAlikes.NamesEqual("Nitebird", "NITEBIRD"));
        Assert.True(LookAlikes.NamesEqual("B0B1", "b0b1"));
        Assert.False(LookAlikes.NamesEqual("Nitebird", "N1TEB1RD"));   // 1 for I: no tolerance any more
        Assert.False(LookAlikes.NamesEqual("BOBI", "B0B1"));           // O for 0, I for 1
        Assert.False(LookAlikes.NamesEqual("B0B1", "BOBI"));
        Assert.False(LookAlikes.NamesEqual(null, "x"));
        Assert.False(LookAlikes.NamesEqual("x", null));
    }
}
