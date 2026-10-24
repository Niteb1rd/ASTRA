using System.Text.Json;
using OpenCvSharp;

namespace Iris.Core.Tests;

/// <summary>What the Python reference found as one nameplate in a synthetic frame: the name, its rows (empty for a name on one row), and the plate colour.</summary>
public sealed record ExpectedPlate(string Name, double Score, Rect NameBox, Rect DistanceBox, string DistanceText, Rect[] Rows, string? Color, double Hue, double Saturation);

/// <summary>The evaluation of one candidate pair in the reference: is it a nameplate, which name (null: a nameplate without a name), which rows and colour.</summary>
public sealed record ExpectedPlateEvaluation(Rect Top, Rect Bottom, string Kind, bool PairIsNameplate, bool IsNameplate, string? Name, Rect[] Rows, string? Color);

/// <summary>The rows stacked above the name line of a pair (top to bottom, the name line last) and the lines that could not join them.</summary>
public sealed record ExpectedStack(Rect Top, Rect Bottom, Rect[] Rows, Rect[] Blockers);

public sealed record PlateFixtureFrame(
    string File, int Width, int Height, Rect[] Boxes, ExpectedStack[] Stacks, ExpectedPlateEvaluation[] Evaluations, ExpectedPlate[] Detections)
{
    public string ImagePath => Path.Combine(PlateFixtures.Dir, File);
}

/// <summary>
/// Loads tests/Iris.Core.Tests/Fixtures/plates (copied next to the test assembly by the build): synthetic frames with multi-row names and plates of every colour, drawn by
/// make_plate_fixtures.py in that folder with made-up words, and what the Python reference detector (detector_ref.py, o0 rule on) made of them.
/// </summary>
public static class PlateFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "plates");

    public static JsonElement Parameters { get; private set; }

    public static IReadOnlyList<PlateFixtureFrame> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "plates.json")));
        Parameters = doc.RootElement.GetProperty("params").Clone();
        var frames = new List<PlateFixtureFrame>();
        foreach (var e in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            var scan = e.GetProperty("default");
            frames.Add(new PlateFixtureFrame(
                e.GetProperty("file").GetString()!,
                e.GetProperty("width").GetInt32(),
                e.GetProperty("height").GetInt32(),
                Rects(scan.GetProperty("boxes")),
                scan.GetProperty("stacks").EnumerateArray().Select(s => new ExpectedStack(
                    Rect(s.GetProperty("top")), Rect(s.GetProperty("bottom")), Rects(s.GetProperty("rows")), Rects(s.GetProperty("blockers")))).ToArray(),
                scan.GetProperty("evaluations").EnumerateArray().Select(v => new ExpectedPlateEvaluation(
                    Rect(v.GetProperty("top")),
                    Rect(v.GetProperty("bottom")),
                    v.GetProperty("kind").GetString()!,
                    v.GetProperty("pair_is_nameplate").GetBoolean(),
                    v.GetProperty("is_nameplate").GetBoolean(),
                    Text(v.GetProperty("name")),
                    Rects(v.GetProperty("rows")),
                    Text(v.GetProperty("color")))).ToArray(),
                scan.GetProperty("detections").EnumerateArray().Select(d => new ExpectedPlate(
                    d.GetProperty("name").GetString()!,
                    d.GetProperty("score").GetDouble(),
                    Rect(d.GetProperty("name_box")),
                    Rect(d.GetProperty("dist_box")),
                    d.GetProperty("dist_text").GetString()!,
                    Rects(d.GetProperty("rows")),
                    Text(d.GetProperty("color")),
                    d.GetProperty("hue").GetDouble(),
                    d.GetProperty("sat").GetDouble())).ToArray()));
        }

        return frames;
    }

    private static string? Text(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    private static Rect Rect(JsonElement a) => new(a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32());

    private static Rect[] Rects(JsonElement a) => a.EnumerateArray().Select(Rect).ToArray();
}
