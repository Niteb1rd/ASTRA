using Iris.Core.Detection.Letters;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>The letter classifier alone (no image code, so these run on every system).</summary>
public class LetterModelTests
{
    private const string EmbeddedName = "Iris.Core.Detection.Letters.letters-model.bin";

    [Fact]
    public void EmbeddedModel_HasTheExpectedShape()
    {
        var model = LetterModel.LoadEmbedded();

        Assert.Equal(NameplateFeatures.VectorLength, model.InputSize);
        Assert.Equal(388, model.InputSize);
        Assert.Equal("-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_km", model.Charset);
        Assert.Equal(41, model.ClassCount);
    }

    /// <summary>PARITY WITH PYTHON: 200 synthetic vectors, log probabilities within 1e-4 of the Python reference.</summary>
    [Fact]
    public void Parity_LogProbabilities_MatchPythonWithin1e4()
    {
        var model = LetterModel.LoadEmbedded();
        var input = LetterFixtures.ReadFloat32("vectors-input-f32.bin");
        var expected = LetterFixtures.ReadFloat64("vectors-logp-f64.bin");
        var n = model.InputSize;
        var classes = model.ClassCount;
        var count = input.Length / n;

        Assert.Equal(200, count);
        Assert.Equal(count * classes, expected.Length);

        var vector = new double[n];
        var logProbs = new double[classes];
        var worst = 0.0;
        for (var i = 0; i < count; i++)
        {
            for (var k = 0; k < n; k++)
                vector[k] = input[i * n + k];
            model.PredictLogProbs(vector, logProbs);

            var bestExpected = 0;
            var bestActual = 0;
            for (var j = 0; j < classes; j++)
            {
                worst = Math.Max(worst, Math.Abs(logProbs[j] - expected[i * classes + j]));
                if (expected[i * classes + j] > expected[i * classes + bestExpected])
                    bestExpected = j;
                if (logProbs[j] > logProbs[bestActual])
                    bestActual = j;
            }

            Assert.Equal(bestExpected, bestActual);
        }

        Assert.True(worst < 1e-4, $"largest log probability difference {worst:G3}");
    }

    [Fact]
    public void PredictLogProbs_RejectsWrongInputLength()
    {
        var model = LetterModel.LoadEmbedded();

        Assert.Throws<ArgumentException>(() => model.PredictLogProbs(new double[10], new double[model.ClassCount]));
        Assert.Throws<ArgumentException>(() => model.PredictLogProbs(new double[model.InputSize], new double[3]));
    }

    [Fact]
    public void Load_RejectsDamagedFiles()
    {
        var bytes = EmbeddedBytes();

        var wrongMagic = (byte[])bytes.Clone();
        wrongMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => LetterModel.Load(new MemoryStream(wrongMagic)));

        var truncated = bytes.Take(bytes.Length / 2).ToArray();
        Assert.Throws<InvalidDataException>(() => LetterModel.Load(new MemoryStream(truncated)));

        var extra = bytes.Concat(new byte[] { 1 }).ToArray();
        Assert.Throws<InvalidDataException>(() => LetterModel.Load(new MemoryStream(extra)));
    }

    private static byte[] EmbeddedBytes()
    {
        using var stream = typeof(LetterModel).Assembly.GetManifestResourceStream(EmbeddedName)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
