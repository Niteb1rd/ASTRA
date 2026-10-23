using System.Runtime.InteropServices;
using Iris.Core.Detection.Letters;
using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// PARITY WITH PYTHON for the nameplate reader. The fixtures are synthetic lines (see LetterFixtures); the expected values come from
/// the Python reference (OpenCV 5.0 in Python, OpenCvSharp 4.11 here), stage by stage, so a difference is traced to the stage
/// where it begins:
///   1. mask from the image      (OpenCV)   equal, or at least 99.5% of the pixels equal
///   2. capital band, cuts       (managed)  exactly equal, starting from Python's mask
///   3. letter vectors           (OpenCV)   within 1e-4, starting from Python's mask
///   4. read text and score      (both)     from Python's mask: text equal, score within 1e-3; from the image: text equal
/// </summary>
public class NameplateReaderTests
{
    private const double MinMaskAgreement = 0.995;
    private const int MaxMaskSizeDifference = 1;

    private static readonly LetterModel Model = LetterModel.LoadEmbedded();

    [Fact]
    public void Fixtures_AreThere()
    {
        var lines = LetterFixtures.LoadLines();

        Assert.Equal(30, lines.Count);
        Assert.All(lines, l => Assert.True(File.Exists(l.ImagePath), l.ImagePath));
        Assert.Contains(lines, l => l.Mask is null);          // one line without any text
        Assert.Contains(lines, l => l.Text.Contains('_') || l.Text.Contains('-'));
    }

    /// <summary>
    /// Stage 1. The mask may differ in a few edge pixels if another OpenCV build rounds differently: its size may differ by 1 row
    /// or column, and at least 99.5% of the pixels of the overlapping area must be equal. It must not differ more.
    /// </summary>
    [OpenCvFact]
    public void Stage1_Mask_MatchesPython()
    {
        var problems = new List<string>();
        var worst = 1.0;
        var exact = 0;
        foreach (var line in LetterFixtures.LoadLines())
        {
            using var image = Cv2.ImRead(line.ImagePath, ImreadModes.Color);
            var mask = NameplateFeatures.CreateMask(image, line.Box);

            if (line.Mask is null)
            {
                if (mask is not null)
                    problems.Add($"{line.Id}: expected no mask");
                continue;
            }

            // The trimmed mask may be one row or column larger or smaller if an edge pixel falls on the other side of the threshold:
            // up to 1 of difference is tolerated and only the overlapping area (from the top left corner) is compared.
            // Anything bigger fails the line.
            if (mask is null || Math.Abs(mask.Height - line.Mask.Height) > MaxMaskSizeDifference || Math.Abs(mask.Width - line.Mask.Width) > MaxMaskSizeDifference)
            {
                problems.Add($"{line.Id}: mask size {(mask is null ? "none" : $"{mask.Width}x{mask.Height}")}, expected {line.Mask.Width}x{line.Mask.Height}");
                continue;
            }

            var overlapHeight = Math.Min(mask.Height, line.Mask.Height);
            var overlapWidth = Math.Min(mask.Width, line.Mask.Width);
            var equal = 0;
            for (var y = 0; y < overlapHeight; y++)
                for (var x = 0; x < overlapWidth; x++)
                    if (mask[y, x] == line.Mask[y, x])
                        equal++;
            var agreement = (double)equal / (overlapHeight * overlapWidth);
            worst = Math.Min(worst, agreement);
            if (equal == mask.Data.Length && mask.Data.Length == line.Mask.Data.Length)
                exact++;
            if (agreement < MinMaskAgreement)
                problems.Add($"{line.Id}: only {agreement:P2} of the mask pixels equal");
        }

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)} (masks exactly equal: {exact}, worst agreement {worst:P3})");
    }

    /// <summary>Stage 2, from Python's mask, so it cannot be disturbed by stage 1.</summary>
    [Fact]
    public void Stage2_CapBandAndCuts_MatchPythonExactly()
    {
        foreach (var line in LetterFixtures.LoadLines().Where(l => l.Mask is not null))
        {
            var mask = line.Mask!;

            Assert.Equal(line.CapBand!.Value, NameplateFeatures.FindCapBand(mask));
            Assert.Equal(line.Cuts, NameplateFeatures.GapCuts(mask).ToArray());
        }
    }

    /// <summary>Stage 3: the first two single letters of each line.</summary>
    [OpenCvFact]
    public void Stage3_LetterVectors_MatchPython()
    {
        var checkedVectors = 0;
        foreach (var line in LetterFixtures.LoadLines().Where(l => l.Mask is not null))
        {
            foreach (var glyph in line.Glyphs)
            {
                var vector = NameplateFeatures.GlyphVector(line.Mask!, glyph.Start, glyph.End, line.CapBand!.Value);

                Assert.NotNull(vector);
                Assert.Equal(glyph.Vector.Length, vector!.Length);
                for (var i = 0; i < vector.Length; i++)
                    Assert.True(Math.Abs(vector[i] - glyph.Vector[i]) < 1e-4, $"{line.Id} glyph {glyph.Start}: value {i} is {vector[i]}, Python {glyph.Vector[i]}");
                checkedVectors++;
            }
        }

        Assert.True(checkedVectors >= 30, $"only {checkedVectors} vectors were checked");
    }

    /// <summary>Stage 4a: read from Python's mask. This covers the cutting search (_split), which the fixtures exercise.</summary>
    [OpenCvFact]
    public void Stage4_ReadFromPythonMask_MatchesPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var line in LetterFixtures.LoadLines().Where(l => l.Mask is not null))
        {
            var read = reader.ReadMask(line.Mask!);

            if (read.Text != line.Text)
                problems.Add($"{line.Id}: '{read.Text}', Python '{line.Text}'");
            else if (Math.Abs(read.Score - line.Score) > 1e-3)
                problems.Add($"{line.Id}: score {read.Score}, Python {line.Score}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>Stage 4b: the whole path from the image. Text must be equal for every line. The score is not compared: it depends on the mask, which may differ at the edges.</summary>
    [OpenCvFact]
    public void Stage4_ReadFromImage_MatchesPython()
    {
        var reader = new NameplateReader(Model);
        var problems = new List<string>();
        foreach (var line in LetterFixtures.LoadLines())
        {
            using var image = Cv2.ImRead(line.ImagePath, ImreadModes.Color);
            var read = reader.Read(image, line.Box);

            if (read.Text != line.Text)
                problems.Add($"{line.Id}: '{read.Text}', Python '{line.Text}'");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The pixel buffer overload (rows with padding) gives exactly the same as the Mat overload.</summary>
    [OpenCvFact]
    public void ByteBufferOverload_GivesTheSameAsMat()
    {
        var reader = new NameplateReader(Model);
        foreach (var line in LetterFixtures.LoadLines().Take(12))
        {
            using var image = Cv2.ImRead(line.ImagePath, ImreadModes.Color);
            var width = image.Width;
            var height = image.Height;
            var stride = width * 3 + 7;
            var tight = new byte[width * height * 3];
            Marshal.Copy(image.Data, tight, 0, tight.Length);
            var buffer = new byte[stride * height];
            Array.Fill(buffer, (byte)0xAA);
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(tight, y * width * 3, buffer, y * stride, width * 3);

            var fromMat = NameplateFeatures.CreateMask(image, line.Box);
            var fromBuffer = NameplateFeatures.CreateMask(buffer, width, height, stride, line.Box);

            Assert.Equal(fromMat is null, fromBuffer is null);
            if (fromMat is not null)
            {
                Assert.Equal(fromMat.Height, fromBuffer!.Height);
                Assert.Equal(fromMat.Width, fromBuffer.Width);
                Assert.Equal(fromMat.Data, fromBuffer.Data);
            }

            Assert.Equal(reader.Read(image, line.Box), reader.Read(buffer, width, height, stride, line.Box));
        }
    }

    /// <summary>
    /// Benchmark-like, but it asserts no timing (timings on a build machine are flaky). IRIS reads each visible nameplate 5 to 10 times a
    /// second, so the reader reuses its Mats and buffers within a call (see NameplateReader.ReadMask: one GlyphVector + PredictLogProbs
    /// per separate piece, one per candidate span in a split, none of them allocating). This runs every line many times through one
    /// shared reader and checks that nothing is carried over from one call to the next. To measure, run it in Release and time it.
    /// </summary>
    [OpenCvFact]
    public void ReadingTheSameLineRepeatedly_GivesTheSameResult()
    {
        var reader = new NameplateReader(Model);
        foreach (var line in LetterFixtures.LoadLines().Where(l => l.Mask is not null).Take(10))
        {
            var first = reader.ReadMask(line.Mask!);
            for (var repeat = 0; repeat < 50; repeat++)
                Assert.Equal(first, reader.ReadMask(line.Mask!));
        }
    }

    [OpenCvFact]
    public void BoxOutsideTheImage_GivesNothing()
    {
        var reader = new NameplateReader(Model);
        using var image = new Mat(40, 80, MatType.CV_8UC3, new Scalar(10, 10, 10));

        Assert.Equal(NameplateRead.Empty, reader.Read(image, new Rect(500, 500, 30, 12)));
        Assert.Equal(NameplateRead.Empty, reader.Read(image, new Rect(5, 5, 30, 12))); // flat image: no text
    }

    /// <summary>
    /// The piece by piece read says the same as the plain read: same text, same total score. Only for lines the work limits leave
    /// alone: <see cref="NameplateReader.ReadPieces(LineMask, bool[])"/> refuses to cut a run of ink wider than
    /// <see cref="NameplateReader.MaxPieceCaps"/> capital heights (or with too many candidate spans), the plain read does not.
    /// </summary>
    [OpenCvFact]
    public void ReadPieces_SumUpToTheSameAsReadMask()
    {
        var reader = new NameplateReader(Model);
        var compared = 0;
        foreach (var line in LetterFixtures.LoadLines().Where(l => l.Mask is not null))
        {
            var band = NameplateFeatures.FindCapBand(line.Mask!);
            var cap = band.Bottom - band.Top + 1;
            if (NameplateFeatures.GapCuts(line.Mask!).Any(c => c.End - c.Start > NameplateReader.MaxPieceCaps * cap))
                continue; // a run the work limit refuses to cut
            compared++;

            var plain = reader.ReadMask(line.Mask!);
            var pieces = reader.ReadPieces(line.Mask!);

            Assert.Equal(plain.Text, string.Concat(pieces.Pieces.Select(p => p.Text)));
            Assert.Equal(plain.Score, pieces.Pieces.Sum(p => p.Score), 9);
            Assert.All(pieces.Pieces, p => Assert.Equal(p.Score, p.FreeScore)); // no restriction: nothing to bend
            Assert.Equal(NameplateFeatures.FindCapBand(line.Mask!).Height, pieces.CapHeight);
            for (var i = 1; i < pieces.Pieces.Count; i++)
                Assert.True(pieces.Pieces[i].Start >= pieces.Pieces[i - 1].End);
        }

        Assert.True(compared >= 10, $"only {compared} fixture lines were within the work limits");
    }

    /// <summary>With the alphabet restricted to digits and a few signs, nothing else comes out, and the free score is never worse.</summary>
    [OpenCvFact]
    public void ReadPieces_WithAnAlphabet_ChoosesOnlyFromIt()
    {
        var reader = new NameplateReader(Model);
        const string allowed = "0123456789KM-_";
        var alphabet = reader.CreateAlphabet(allowed);
        Assert.Equal(Model.ClassCount, alphabet.Length);

        var bent = 0;
        foreach (var line in LetterFixtures.LoadLines().Where(l => l.Mask is not null))
        {
            var pieces = reader.ReadPieces(line.Mask!, alphabet);

            foreach (var piece in pieces.Pieces)
            {
                Assert.All(piece.Text, c => Assert.Contains(c, allowed));
                if (piece.Text.Length == 1)
                {
                    Assert.True(piece.FreeScore >= piece.Score - 1e-12);
                    if (piece.FreeScore > piece.Score + 1e-6)
                        bent++;
                }
            }
        }

        Assert.True(bent > 0, "none of the lines had a letter that the restriction had to change");
    }

    [Fact]
    public void CreateAlphabet_NeedsCharactersOfTheModel()
    {
        var reader = new NameplateReader(Model);

        Assert.Throws<ArgumentException>(() => reader.CreateAlphabet("abc")); // lower case a, b, c: not in the charset (only k and m are, with the dot)
        Assert.Throws<ArgumentException>(() => reader.CreateAlphabet(""));
        Assert.Equal(3, reader.CreateAlphabet("A.B").Count(a => a));
        Assert.Equal(3, reader.CreateAlphabet(".km").Count(a => a));
    }

    /// <summary>A long run of touching ink is noise, not letters: it is given up on at once, not cut thousands of ways.</summary>
    [OpenCvFact]
    public void ReadPieces_GivesUpOnARunWiderThanEightCapitalHeights()
    {
        var reader = new NameplateReader(Model);
        var solid = new LineMask(20, 400, Enumerable.Repeat((byte)1, 20 * 400).ToArray());

        var pieces = reader.ReadPieces(solid);

        var piece = Assert.Single(pieces.Pieces);
        Assert.Equal(string.Empty, piece.Text);
        Assert.Equal(-50.0, piece.Score);
    }
}
