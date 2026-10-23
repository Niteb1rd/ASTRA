using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// The distance rule works on what the model read, not on pixels, so these tests need no OpenCV: the pieces are written out by
/// hand the way the restricted reader returns them (digits, K, M, "-" and "_"; a dot is a "-" or "_" or missing; the lower case unit
/// letters come out as K, "-", M or a digit). 48 is a typical capital height in mask pixels (a 12 pixel line at 4x).
/// </summary>
public class DistanceLineRuleTests
{
    private const int Cap = 48;

    private static readonly DistanceLineRule Rule = new(NameplateDetectorSettings.Default);

    /// <summary>Pieces from a compact description: "text@start-end" separated by spaces; each piece is surest (score 0).</summary>
    private static List<ReadPiece> Pieces(string description)
    {
        var list = new List<ReadPiece>();
        foreach (var part in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('@');
            var span = part[(at + 1)..].Split('-', 2);
            list.Add(new ReadPiece(int.Parse(span[0]), int.Parse(span[1]), part[..at], 0.0, 0.0));
        }

        return list;
    }

    private static DistanceVerdict Verdict(string description) => Rule.Evaluate(Pieces(description), Cap);

    [Fact]
    public void KilometersWithDecimalPoint_AreFull()
    {
        // "1.9km": 1 . 9 k m
        Assert.Equal(DistanceVerdict.Full, Verdict("1@0-26 -@35-44 9@51-81 K@87-112 -@115-162"));
    }

    [Fact]
    public void DroppedDecimalPoint_IsStillFull()
    {
        // the dot was a speck and was dropped: "5.9km" reads 5 9 k m with a wide gap in the middle
        Assert.Equal(DistanceVerdict.Full, Verdict("5@0-29 9@52-83 K@88-112 -@117-163"));
    }

    [Fact]
    public void Meters_AreFull()
    {
        // "835.5m": the m reads as "-"
        Assert.Equal(DistanceVerdict.Full, Verdict("8@0-32 3@40-68 5@76-105 -@112-119 5@128-158 -@164-210"));
    }

    [Fact]
    public void SpeedInBracketsAfterASpace_DoesNotSpoilIt()
    {
        // "9.0km [15m/s]": the bracket part starts after a gap of 24 pixels, half a capital height
        Assert.Equal(DistanceVerdict.Full, Verdict("9@0-31 -@37-45 0@52-82 K@88-113 -@116-162 6@190-203 1@210-240 5@247-277 M@284-330 -@335-380"));
    }

    [Fact]
    public void ThinOneReadAsAnotherLetter_IsTakenForADigit()
    {
        // "1.7km" where the thin 1 (9 pixels, under a third of the capital height) was read as M
        Assert.Equal(DistanceVerdict.Full, Verdict("M@0-9 -@18-24 7@34-62 K@70-94 -@99-145"));
    }

    [Fact]
    public void NumberWithoutAUnit_IsPartial()
    {
        Assert.Equal(DistanceVerdict.Partial, Verdict("0@0-30 -@36-45 0@52-82"));   // "0.0": the speed readout of the HUD
        Assert.Equal(DistanceVerdict.Partial, Verdict("6@0-30 3@38-68 4@75-105"));   // a number whose unit is cut off
    }

    [Fact]
    public void NumberFollowedByADigitInsteadOfAUnit_IsPartial()
    {
        // the m of "654.0m" came out as two digit-like pieces
        Assert.Equal(DistanceVerdict.Partial, Verdict("6@0-32 5@39-69 4@76-105 -@112-121 0@128-159 0@164-229"));
    }

    [Fact]
    public void LettersReadAsLetterLikeShapes_AreRejected()
    {
        Assert.Equal(DistanceVerdict.Reject, Verdict("K@0-30 M@36-70 M@76-110 3@116-146 K@152-182"));  // "WHISKEY" forced into the alphabet
        Assert.Equal(DistanceVerdict.Reject, Verdict("-@0-30 -@36-70"));                                  // no digit at all
    }

    [Fact]
    public void SpeedLineGluedToItsUnit_IsRejected()
    {
        // "209m/s(9.05km)" without spaces: the unit is followed by more than 6 glued characters
        Assert.Equal(DistanceVerdict.Reject, Verdict("2@0-30 0@36-66 9@72-102 M@108-150 -@156-170 5@176-206 -@212-222 9@228-258 -@264-272 0@278-308 5@314-344 K@350-374 M@380-424"));
    }

    [Fact]
    public void ReadBentFarFromTheModelsOwnOpinion_IsRejected()
    {
        // a line of letters that the restriction turned into digits: the model's free best is much surer than the allowed best
        var bent = new List<ReadPiece>
        {
            new(0, 30, "1", -6.0, -0.1),
            new(36, 66, "-", -6.0, -0.1),
            new(72, 102, "9", -6.0, -0.1),
            new(108, 132, "K", -6.0, -0.1),
            new(138, 184, "-", -6.0, -0.1),
        };

        Assert.Equal(DistanceVerdict.Reject, Rule.Evaluate(bent, Cap));
    }

    [Fact]
    public void EmptyRead_IsRejected()
    {
        Assert.Equal(DistanceVerdict.Reject, Rule.Evaluate(new List<ReadPiece>(), Cap));
        Assert.Equal(DistanceVerdict.Reject, Rule.Evaluate(null));
        Assert.Equal(DistanceVerdict.Reject, Verdict("@0-40")); // a piece that could not be cut: no characters
    }

    [Fact]
    public void LeadingDecimalPoint_IsAccepted()
    {
        // ".4km" (the 0 in front is lost); the dot reads as "-" and is wider than a third of the capital height
        Assert.Equal(DistanceVerdict.Full, Verdict("-@0-20 4@26-56 K@62-86 -@92-138"));
    }
}
