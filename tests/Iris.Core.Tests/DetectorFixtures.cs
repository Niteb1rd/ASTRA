using System.Text.Json;
using OpenCvSharp;

namespace Iris.Core.Tests;

/// <summary>What the Python reference detector found in one synthetic frame, for one set of settings.</summary>
public sealed record ExpectedDetection(string Name, double Score, Rect NameBox, Rect DistanceBox, string DistanceText, bool Stacked);

public sealed record ExpectedScan(Rect[] Boxes, (int Top, int Bottom)[] Pairs, ExpectedDetection[] Detections);

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
        e.GetProperty("detections").EnumerateArray().Select(d => new ExpectedDetection(
            d.GetProperty("name").GetString()!,
            d.GetProperty("score").GetDouble(),
            ReadRect(d.GetProperty("name_box")),
            ReadRect(d.GetProperty("dist_box")),
            d.GetProperty("dist_text").GetString()!,
            d.GetProperty("stacked").GetBoolean())).ToArray());
}
