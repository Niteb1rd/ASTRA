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

    /// <summary>"9.0km" (5 pieces, 6 pixel gaps), then a 24 pixel gap (a word gap is 0.33 * 48 = 15.8), then a bracket piece and the speed group.</summary>
    private static PieceRead SpeedLine(string distance, int bracketAt = 0, params (string Text, int Start, int End)[] after)
    {
        var line = Line(distance);
        var list = line.Pieces.ToList();
        var end = list[^1].End;
        var start = bracketAt > 0 ? bracketAt : end + 24;
        list.Add(new ReadPiece(start, start + 10, "1", 0.0, 0.0)); // what the distance alphabet makes of "[": a digit
        foreach (var piece in after)
            list.Add(new ReadPiece(piece.Start, piece.End, piece.Text, -2.0, -0.1)); // the speed group is read with the distance alphabet: junk
        return new PieceRead(list, Cap);
    }

    private static Func<ReadPiece, bool> BracketAt(int start) => piece => piece.Start == start;

    [Theory]
    [InlineData("9.0km", 9.0, true)]
    [InlineData("4.4km", 4.4, true)]
    [InlineData("835.5m", 835.5, false)]
    public void SpeedGroupAfterTheUnit_IsAllowed_AndNotRead(string distance, double value, bool km)
    {
        var read = SpeedLine(distance, 0, ("2", 300, 330), ("9", 336, 366), ("m", 372, 420), ("5", 800, 830));
        var bracket = read.Pieces[Line(distance).Pieces.Count].Start;

        var reading = DistanceLineRule.Evaluate(read, Settings, BracketAt(bracket));

        Assert.NotNull(reading);
        Assert.True(reading!.Value.HasSpeedGroup);
        Assert.Equal(distance, reading.Value.Text); // the text is the distance part only
        Assert.Equal(value, reading.Value.Value, 3);
        Assert.Equal(km, reading.Value.Kilometers);
    }

    [Fact]
    public void PlainDistance_HasNoSpeedGroup()
    {
        var reading = DistanceLineRule.Evaluate(Line("9.0km"), Settings, _ => true);

        Assert.NotNull(reading);
        Assert.False(reading!.Value.HasSpeedGroup);
    }

    [Fact]
    public void GapAfterTheUnit_WithoutABracket_IsNotADistance()
    {
        var read = SpeedLine("9.0km", 0, ("2", 300, 330));

        Assert.Null(DistanceLineRule.Evaluate(read, Settings));                 // no bracket test at all
        Assert.Null(DistanceLineRule.Evaluate(read, Settings, _ => false));     // the piece after the gap is not a "["
    }

    [Fact]
    public void SpeedGroupWithoutADistanceBeforeIt_IsNotAnAnchor()
    {
        // "[-29m/s]" alone, or after something that is not number.digit unit: the bracket after the gap cannot rescue the part before it
        var noDecimal = SpeedLine("12km", 0);
        Assert.Null(DistanceLineRule.Evaluate(noDecimal, Settings, _ => true));
        var speedFirst = SpeedLine("209m", 0);   // "209m/s (9.05km)": speed first, uppercase and parentheses in the game
        Assert.Null(DistanceLineRule.Evaluate(speedFirst, Settings, _ => true));
        var word = SpeedLine("WHISKEY", 0);
        Assert.Null(DistanceLineRule.Evaluate(word, Settings, _ => true));
    }

    [Fact]
    public void OnlyTheFirstGapMayHoldTheBracket()
    {
        // a second wide gap before the bracket: "9.0km" gap "7" gap "[": the piece after the FIRST gap is not the bracket
        var line = Line("9.0km");
        var list = line.Pieces.ToList();
        var end = list[^1].End;
        list.Add(new ReadPiece(end + 24, end + 54, "7", 0.0, 0.0));
        list.Add(new ReadPiece(end + 100, end + 110, "1", 0.0, 0.0));
        var read = new PieceRead(list, Cap);

        Assert.Null(DistanceLineRule.Evaluate(read, Settings, BracketAt(end + 100)));
    }

    [Fact]
    public void DistancePartIsStillJudgedStrictly_WithASpeedGroup()
    {
        // The five distance pieces are unsure but not specks: their score (-0.5) is above DistanceEndScore (-1.0), so none is trimmed from the ends,
        // and the 24 pixel gap before the "[" (a word gap is 0.33 * 48 = 15.8) opens the speed-group path: the line is cut after the "m" and judged
        // on those five pieces alone. Their mean score, -0.5, is below MinDistanceScore (-0.35): rejected. (At -2.0 the pieces would be trimmed
        // as specks before the bracket is ever looked at, and the test would not reach this path.)
        static PieceRead Pieces(double score) => new(new List<ReadPiece>
        {
            new(0, 30, "9", score, -0.1),
            new(36, 45, ".", score, -0.1),
            new(51, 81, "0", score, -0.1),
            new(87, 117, "k", score, -0.1),
            new(123, 153, "m", score, -0.1),
            new(177, 187, "1", 0.0, 0.0),
        }, Cap);

        Assert.Null(DistanceLineRule.Evaluate(Pieces(-0.5), Settings, BracketAt(177)));

        // the same line with sure pieces (mean -0.2, free score only 0.1 above) is accepted through the very same path: the speed group is found
        var reading = DistanceLineRule.Evaluate(Pieces(-0.2), Settings, BracketAt(177));
        Assert.NotNull(reading);
        Assert.True(reading!.Value.HasSpeedGroup);
        Assert.Equal("9.0km", reading.Value.Text);
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
