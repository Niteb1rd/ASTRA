using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The distance anchor rule works on what the model read, not on pixels, so these tests need no OpenCV: the pieces are written out by
/// hand the way the restricted reader returns them. 48 is a typical capital height in mask pixels (a 12 pixel line at 4x).
/// </summary>
public class DistanceLineRuleTests
{
    private const int Cap = 48;

    private static readonly NameplateDetectorSettings Settings = NameplateDetectorSettings.Default;

    private static PieceRead Read(params (string Text, int Start, int End)[] pieces) =>
        new(pieces.Select(p => new ReadPiece(p.Start, p.End, p.Text, 0.0, 0.0)).ToList(), Cap);

    /// <summary>A line of single characters, 30 wide with a 6 pixel gap.</summary>
    private static PieceRead Line(string text)
    {
        var list = new List<ReadPiece>();
        var x = 0;
        foreach (var c in text)
        {
            var width = c == '.' ? 9 : 30;
            list.Add(new ReadPiece(x, x + width, c.ToString(), 0.0, 0.0));
            x += width + 6;
        }

        return new PieceRead(list, Cap);
    }

    [Theory]
    [InlineData("835.5m", 835.5, false)]
    [InlineData("1.9km", 1.9, true)]
    [InlineData("0.0m", 0.0, false)]
    [InlineData("1000.0m", 1000.0, false)]
    [InlineData("12.3km", 12.3, true)]
    public void NumberPointDigitUnit_IsADistance(string text, double value, bool km)
    {
        var reading = DistanceLineRule.Evaluate(Line(text), Settings);
        Assert.NotNull(reading);
        Assert.Equal(text, reading!.Value.Text);
        Assert.Equal(value, reading.Value.Value, 3);
        Assert.Equal(km, reading.Value.Kilometers);
    }

    [Theory]
    [InlineData("835m")]        // no decimal: ignored
    [InlineData("835.55m")]     // two decimals
    [InlineData("835.5")]       // no unit
    [InlineData("835.5mm")]
    [InlineData("835.5M")]      // the unit is lower case
    [InlineData("1001.0m")]     // above 1000
    [InlineData("12345.0m")]    // five digits
    [InlineData(".5m")]         // no number
    [InlineData("8.5km/s")]     // something after the unit
    [InlineData("WHISKEY")]
    public void OtherShapes_AreNotADistance(string text)
    {
        Assert.Null(DistanceLineRule.Evaluate(Line(text), Settings));
    }

    [Fact]
    public void DigitLookAlikes_AreTurnedIntoDigits()
    {
        // O->0, Q->0, D->0, I->1, S->5, B->8, G->6, Z->2, and 7 stays 7
        var reading = DistanceLineRule.Evaluate(Line("OQ.Skm"), Settings);
        Assert.Equal("00.5km", reading!.Value.Text);
        reading = DistanceLineRule.Evaluate(Line("BG.Zm"), Settings);
        Assert.Equal("86.2m", reading!.Value.Text);
        reading = DistanceLineRule.Evaluate(Line("I7.Dm"), Settings);
        Assert.Equal("17.0m", reading!.Value.Text);
    }

    [Fact]
    public void WideGapInsideTheLine_IsNotOneDistance()
    {
        // "8.1" ... wide gap ... "m": two things, not a distance
        var read = Read(("8", 0, 30), (".", 36, 45), ("1", 51, 81), ("m", 200, 260));
        Assert.Null(DistanceLineRule.Evaluate(read, Settings));
    }

    [Fact]
    public void UnsureEndPieces_AreSpecksNextToTheLine()
    {
        // a speck read as "-" with a very bad score in front, and another behind
        var pieces = new List<ReadPiece>
        {
            new(0, 6, "I", -3.0, -0.2),
            new(30, 60, "7", 0.0, 0.0),
            new(66, 75, ".", 0.0, 0.0),
            new(81, 111, "4", 0.0, 0.0),
            new(117, 147, "m", 0.0, 0.0),
            new(160, 166, "I", -3.0, -0.2),
        };
        var reading = DistanceLineRule.Evaluate(new PieceRead(pieces, Cap), Settings);
        Assert.Equal("7.4m", reading!.Value.Text);
    }

    [Fact]
    public void ReadBentFarFromTheModelsOwnOpinion_IsRejected()
    {
        // a line of letters that the restriction turned into a distance: the model's free best is much surer than the allowed best
        // FreeScore is the model's best over all characters: much higher than the allowed score (more than MaxDistanceGap per character)
        var bent = new List<ReadPiece>
        {
            new(0, 30, "1", -5.0, -0.1),
            new(36, 45, ".", -5.0, -0.1),
            new(51, 81, "9", -5.0, -0.1),
            new(87, 117, "m", -5.0, -0.1),
        };
        // such unsure pieces are also trimmed as specks, which leaves nothing: either way it is no distance
        Assert.Null(DistanceLineRule.Evaluate(new PieceRead(bent, Cap), Settings));
    }

    [Fact]
    public void EmptyRead_IsRejected()
    {
        Assert.Null(DistanceLineRule.Evaluate(null, Settings));
        Assert.Null(DistanceLineRule.Evaluate(new PieceRead(new List<ReadPiece>(), Cap), Settings));
    }

    [Fact]
    public void Parse_IsTheShapeCheckAlone()
    {
        Assert.NotNull(DistanceLineRule.Parse("0.5km", 1000));
        Assert.Null(DistanceLineRule.Parse("0.5kmm", 1000));
        Assert.Null(DistanceLineRule.Parse("1001.0km", 1000));
        Assert.Null(DistanceLineRule.Parse("", 1000));
    }
}
