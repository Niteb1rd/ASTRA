using System.Text.Json;
using Iris.Core.Detection.Letters;
using OpenCvSharp;

namespace Iris.Core.Tests;

/// <summary>One single-letter vector recorded by the Python reference.</summary>
public sealed record FixtureGlyph(int Start, int End, double[] Vector);

/// <summary>
/// One synthetic nameplate line and what the Python reference (the prototype's fontrec.py with the exported model) made of it at
/// every stage. The images are drawn by a script on generated backgrounds: nothing in the fixtures comes from the game.
/// </summary>
public sealed record FixtureLine(
    string Id,
    string ImagePath,
    Rect Box,
    LineMask? Mask,
    CapBand? CapBand,
    (int Start, int End)[] Cuts,
    FixtureGlyph[] Glyphs,
    string Text,
    double Score);

/// <summary>Loads tests/Iris.Core.Tests/Fixtures/letters (copied next to the test assembly by the build).</summary>
public static class LetterFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "letters");

    public static IReadOnlyList<FixtureLine> LoadLines()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "lines.json")));
        var lines = new List<FixtureLine>();
        foreach (var e in doc.RootElement.GetProperty("lines").EnumerateArray())
        {
            var b = e.GetProperty("box");
            var box = new Rect(b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32());

            LineMask? mask = null;
            var maskElement = e.GetProperty("mask");
            if (maskElement.ValueKind != JsonValueKind.Null)
            {
                mask = LineMask.FromPackedBits(
                    maskElement.GetProperty("height").GetInt32(),
                    maskElement.GetProperty("width").GetInt32(),
                    Convert.FromBase64String(maskElement.GetProperty("bits").GetString()!));
            }

            CapBand? band = null;
            var bandElement = e.GetProperty("cap_band");
            if (bandElement.ValueKind != JsonValueKind.Null)
                band = new CapBand(bandElement[0].GetInt32(), bandElement[1].GetInt32());

            var cuts = e.GetProperty("cuts").EnumerateArray()
                .Select(c => (c[0].GetInt32(), c[1].GetInt32()))
                .ToArray();

            var glyphs = e.GetProperty("glyphs").EnumerateArray()
                .Select(g => new FixtureGlyph(
                    g.GetProperty("start").GetInt32(),
                    g.GetProperty("end").GetInt32(),
                    g.GetProperty("vec").EnumerateArray().Select(v => v.GetDouble()).ToArray()))
                .ToArray();

            lines.Add(new FixtureLine(
                e.GetProperty("id").GetString()!,
                Path.Combine(Dir, e.GetProperty("image").GetString()!),
                box,
                mask,
                band,
                cuts,
                glyphs,
                e.GetProperty("text").GetString()!,
                e.GetProperty("score").GetDouble()));
        }

        return lines;
    }

    /// <summary>Little endian float32 values, as written by the Python script.</summary>
    public static float[] ReadFloat32(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, file));
        var values = new float[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++)
            values[i] = BitConverter.ToSingle(bytes, i * 4);
        return values;
    }

    /// <summary>Little endian float64 values, as written by the Python script.</summary>
    public static double[] ReadFloat64(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, file));
        var values = new double[bytes.Length / 8];
        for (var i = 0; i < values.Length; i++)
            values[i] = BitConverter.ToDouble(bytes, i * 8);
        return values;
    }
}
