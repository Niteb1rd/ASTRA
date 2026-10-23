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
///   4. read text and score      (both)     text equal, starting from Python's mask and from the image
/// </summary>
public class NameplateReaderTests
{
    private const double MinMaskAgreement = 0.995;

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

    /// <summary>Stage 1. The mask may differ in a few edge pixels if another OpenCV build rounds differently; it must not differ more.</summary>
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

            if (mask is null || mask.Height != line.Mask.Height || mask.Width != line.Mask.Width)
            {
                problems.Add($"{line.Id}: mask size {(mask is null ? "none" : $"{mask.Width}x{mask.Height}")}, expected {line.Mask.Width}x{line.Mask.Height}");
                continue;
            }

            var equal = 0;
            for (var i = 0; i < mask.Data.Length; i++)
                if (mask.Data[i] == line.Mask.Data[i])
                    equal++;
            var agreement = (double)equal / mask.Data.Length;
            worst = Math.Min(worst, agreement);
            if (equal == mask.Data.Length)
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

    /// <summary>Stage 4b: the whole path from the image. Text must be equal for every line; the score may drift a little with the mask.</summary>
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
            else if (Math.Abs(read.Score - line.Score) > 0.05)
                problems.Add($"{line.Id}: score {read.Score}, Python {line.Score}");
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

    [OpenCvFact]
    public void BoxOutsideTheImage_GivesNothing()
    {
        var reader = new NameplateReader(Model);
        using var image = new Mat(40, 80, MatType.CV_8UC3, new Scalar(10, 10, 10));

        Assert.Equal(NameplateRead.Empty, reader.Read(image, new Rect(500, 500, 30, 12)));
        Assert.Equal(NameplateRead.Empty, reader.Read(image, new Rect(5, 5, 30, 12))); // flat image: no text
    }
}
