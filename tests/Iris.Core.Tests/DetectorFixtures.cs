using System.Text.Json;
using OpenCvSharp;

namespace Iris.Core.Tests;

/// <summary>What the Python reference detector found in one synthetic frame, for one set of settings.</summary>
public sealed record ExpectedDetection(string Name, double Score, Rect NameBox, Rect DistanceBox, string DistanceText, bool SpeedGroup);

/// <summary>
/// What evaluating one candidate pair gave in the reference. <paramref name="Kind"/> is "strict", "crowd" or "loose"; <paramref name="PairIsNameplate"/> is the
/// structural evaluation of the pair alone, <paramref name="IsNameplate"/> the evaluation of the frame evaluator (a crowd pair also needs its neighbours to be
/// nameplates); the name is set when the read was sure enough.
/// </summary>
public sealed record ExpectedEvaluation(
    Rect Top, Rect Bottom, string Kind, bool PairIsNameplate, bool IsNameplate, string? Name, double Score, string DistanceText, bool SpeedGroup);

public sealed record ExpectedCrowdPair(int Top, int Bottom, int[] Neighbours);

public sealed record ExpectedScan(
    Rect[] Boxes, (int Top, int Bottom)[] Pairs, (int Top, int Bottom)[] LoosePairs, ExpectedCrowdPair[] CrowdPairs,
    ExpectedEvaluation[] Evaluations, ExpectedDetection[] Detections);

public sealed record DetectorFixtureFrame(string File, int Width, int Height, ExpectedScan Default, ExpectedScan WholeFrame)
{
    public string ImagePath => Path.Combine(DetectorFixtures.Dir, File);
}

/// <summary>
/// Loads tests/Iris.Core.Tests/Fixtures/detector (copied next to the test assembly by the build): synthetic frames drawn by
/// training-data/scripts/make_detector_fixtures.py with made-up names, and what the Python reference detector made of them.
/// </summary>
public static class DetectorFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "detector");

    public static JsonElement Parameters { get; private set; }

    public static IReadOnlyList<DetectorFixtureFrame> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "detector.json")));
        Parameters = doc.RootElement.GetProperty("params").Clone();
        var frames = new List<DetectorFixtureFrame>();
        foreach (var e in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            frames.Add(new DetectorFixtureFrame(
                e.GetProperty("file").GetString()!,
                e.GetProperty("width").GetInt32(),
                e.GetProperty("height").GetInt32(),
                ReadScan(e.GetProperty("default")),
                ReadScan(e.GetProperty("whole"))));
        }

        return frames;
    }

    private static Rect ReadRect(JsonElement a) => new(a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32());

    private static ExpectedScan ReadScan(JsonElement e) => new(
        e.GetProperty("boxes").EnumerateArray().Select(ReadRect).ToArray(),
        e.GetProperty("pairs").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToArray(),
        e.GetProperty("loose").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToArray(),
        e.GetProperty("crowd").EnumerateArray().Select(p => new ExpectedCrowdPair(
            p[0].GetInt32(), p[1].GetInt32(), p[2].EnumerateArray().Select(n => n.GetInt32()).ToArray())).ToArray(),
        e.GetProperty("evaluations").EnumerateArray().Select(v => new ExpectedEvaluation(
            ReadRect(v.GetProperty("top")),
            ReadRect(v.GetProperty("bottom")),
            v.GetProperty("kind").GetString()!,
            v.GetProperty("pair_is_nameplate").GetBoolean(),
            v.GetProperty("is_nameplate").GetBoolean(),
            v.GetProperty("name").ValueKind == JsonValueKind.Null ? null : v.GetProperty("name").GetString(),
            v.GetProperty("score").GetDouble(),
            v.GetProperty("dist_text").GetString()!,
            v.GetProperty("speed_group").GetBoolean())).ToArray(),
        e.GetProperty("detections").EnumerateArray().Select(d => new ExpectedDetection(
            d.GetProperty("name").GetString()!,
            d.GetProperty("score").GetDouble(),
            ReadRect(d.GetProperty("name_box")),
            ReadRect(d.GetProperty("dist_box")),
            d.GetProperty("dist_text").GetString()!,
            d.GetProperty("speed_group").GetBoolean())).ToArray());
}
