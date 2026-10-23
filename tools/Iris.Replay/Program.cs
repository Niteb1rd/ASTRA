using System.Diagnostics;
using System.Globalization;
using System.Text;
using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using OpenCvSharp;

namespace Iris.Replay;

/// <summary>
/// Developer tool: replays recordings through <see cref="NameplateDetector"/> and reports detections and timings, and with a label
/// file the recall and the wrong reads (see README, "Replay tool"). It writes only to the folder given with --out, which must be
/// outside any git repository: recordings, frames and the names in them are not for the repository.
/// </summary>
public static class Program
{
    private const string CsvHeader = "source,frame,name,score,x,y,w,h,distance,dist_x,dist_y,dist_w,dist_h,stacked,match,exact";

    public static int Main(string[] args)
    {
        var options = ReplayOptions.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ReplayOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(ReplayOptions.Usage);
            return 0;
        }

        try
        {
            return Run(options);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException or OpenCVException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static int Run(ReplayOptions options)
    {
        if (IsInsideGitRepository(options.OutDirectory))
        {
            Console.Error.WriteLine($"Refusing to write into {Path.GetFullPath(options.OutDirectory)}: it is inside a git repository. Detections and annotated frames contain other players' handles and game imagery; choose another --out folder.");
            return 2;
        }

        ClipLabels? labels = options.LabelsPath is null ? null : ClipLabels.Load(options.LabelsPath);
        var clips = FrameSources.Discover(options.Input);
        Directory.CreateDirectory(options.OutDirectory);
        var annotatedFolder = Path.Combine(options.OutDirectory, "annotated");
        if (options.Annotate)
            Directory.CreateDirectory(annotatedFolder);

        var model = options.ModelPath is null ? LetterModel.LoadEmbedded() : LetterModel.LoadFile(options.ModelPath);
        var reader = new NameplateReader(model);
        using var detector = new NameplateDetector(reader, options.Settings);
        var metrics = labels is null ? null : new ReplayMetrics(labels, ReadExcludedNames());

        var csvPath = Path.Combine(options.OutDirectory, "detections.csv");
        using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
        csv.WriteLine(CsvHeader);

        var inv = CultureInfo.InvariantCulture;
        var step = options.Every == 1 ? "every frame" : string.Create(inv, $"every {options.Every} frames");
        var settings = options.Settings;
        Console.WriteLine(string.Create(inv, $"Replaying {clips.Count} clip(s), {step}, scan region x {settings.RegionLeft:P0}-{settings.RegionRight:P0}, y {settings.RegionTop:P0}-{settings.RegionBottom:P0}."));
        Console.WriteLine();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,7} {2,10} {3,11} {4,9} {5,10} {6,9}",
            "clip", "frames", "with plate", "nameplates", "distinct", "ms median", "ms p95"));

        var totalFrames = 0;
        var allTimes = new List<double>();
        foreach (var clip in clips)
        {
            var times = new List<double>();
            var withPlates = 0;
            var plates = 0;
            var distinct = new HashSet<string>();
            var unreadable = 0;
            var annotateFailures = new List<string>();
            string? skipped = null;

            try
            {
                foreach (var frame in FrameSources.Frames(clip, options.Every, _ => unreadable++))
                {
                    using var image = frame.Image;
                    var stopwatch = Stopwatch.StartNew();
                    var detections = detector.Detect(image);
                    stopwatch.Stop();
                    times.Add(stopwatch.Elapsed.TotalMilliseconds);

                    IReadOnlyList<(string? Label, bool Exact)> matches = Array.Empty<(string?, bool)>();
                    metrics?.AddFrame(frame.Clip, frame.Name, frame.Number, detections, out matches);

                    if (detections.Count > 0)
                        withPlates++;
                    plates += detections.Count;
                    for (var i = 0; i < detections.Count; i++)
                    {
                        distinct.Add(detections[i].Name);
                        var match = i < matches.Count ? matches[i] : (Label: (string?)null, Exact: false);
                        csv.WriteLine(CsvRow(clip.Clip, frame, detections[i], match.Label, match.Exact, metrics is not null));
                    }

                    if (options.Annotate && detections.Count > 0)
                    {
                        var problem = SaveAnnotated(image, detections, annotatedFolder, clip, frame);
                        if (problem is not null)
                            annotateFailures.Add(problem);
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or OpenCVException)
            {
                // a video that will not open (or breaks) must not stop the other clips
                skipped = e.Message;
            }

            totalFrames += times.Count;
            allTimes.AddRange(times);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,7} {2,10} {3,11} {4,9} {5,10} {6,9}",
                Shorten(clip.Clip, 24), times.Count, withPlates, plates, distinct.Count, Format(Median(times)), Format(Percentile95(times))));
            if (skipped is not null)
                Console.WriteLine($"  (skipped: {skipped})");
            if (unreadable > 0)
                Console.WriteLine($"  ({unreadable} image file(s) of this clip could not be read and were skipped)");
            if (annotateFailures.Count > 0)
                Console.WriteLine($"  ({annotateFailures.Count} annotated image(s) could not be written, the first: {annotateFailures[0]})");
        }

        csv.Flush();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,7} {2,10} {3,11} {4,9} {5,10} {6,9}",
            "ALL", totalFrames, string.Empty, string.Empty, string.Empty, Format(Median(allTimes)), Format(Percentile95(allTimes))));
        Console.WriteLine();
        Console.WriteLine($"Detections: {csvPath}");
        if (options.Annotate)
            Console.WriteLine($"Annotated frames: {annotatedFolder}");

        if (metrics is not null)
        {
            Console.WriteLine();
            if (metrics.Groups.Count == 0)
                Console.WriteLine("The label file does not mention any of the clips or images that were replayed.");
            else
                Console.Write(metrics.Format());
        }

        return 0;
    }

    private static string CsvRow(string clip, SourceFrame frame, NameplateDetection d, string? label, bool exact, bool labeled)
    {
        var inv = CultureInfo.InvariantCulture;
        // a video frame (clip#number) and a single image (named like its clip) stand alone; an image in a folder is folder/file
        var source = frame.Name.Contains('#') || Path.GetFileNameWithoutExtension(frame.Name) == clip ? frame.Name : $"{clip}/{frame.Name}";
        var fields = new[]
        {
            Escape(source),
            frame.Number.ToString(inv),
            Escape(d.Name),
            d.Score.ToString("0.0000", inv),
            d.NameBox.X.ToString(inv), d.NameBox.Y.ToString(inv), d.NameBox.Width.ToString(inv), d.NameBox.Height.ToString(inv),
            Escape(d.DistanceText),
            d.DistanceBox.X.ToString(inv), d.DistanceBox.Y.ToString(inv), d.DistanceBox.Width.ToString(inv), d.DistanceBox.Height.ToString(inv),
            d.Stacked ? "1" : "0",
            labeled ? Escape(label ?? string.Empty) : string.Empty,
            labeled ? (exact ? "1" : "0") : string.Empty,
        };
        return string.Join(',', fields);
    }

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>Writes the annotated PNG. Returns null, or why it could not be written.</summary>
    private static string? SaveAnnotated(Mat image, IReadOnlyList<NameplateDetection> detections, string folder, ClipInput clip, SourceFrame frame)
    {
        using var copy = image.Clone();
        var thickness = Math.Max(1, image.Height / 700);
        var fontScale = Math.Max(0.4, image.Height / 1800.0);
        foreach (var d in detections)
        {
            var color = d.Stacked ? new Scalar(0, 165, 255) : new Scalar(0, 255, 0);
            Cv2.Rectangle(copy, Inflate(d.NameBox, 2), color, thickness);
            Cv2.Rectangle(copy, Inflate(d.DistanceBox, 2), new Scalar(0, 220, 255), thickness);
            var label = $"{d.Name} {d.MeanScore:0.00}{(d.Stacked ? " (stacked)" : string.Empty)}";
            Cv2.PutText(copy, label, new Point(d.NameBox.X, Math.Max(14, d.NameBox.Y - 6)), HersheyFonts.HersheySimplex, fontScale, color, thickness);
        }

        var clipFolder = Path.Combine(folder, MakeSafe(clip.Clip));
        var fileName = clip.VideoPath is null ? Path.GetFileNameWithoutExtension(frame.Name) : $"{frame.Number:D6}";
        var target = Path.Combine(clipFolder, MakeSafe(fileName) + ".png");
        try
        {
            // ImEncode + File.WriteAllBytes instead of ImWrite: ImWrite cannot write paths with non-ASCII characters on Windows
            if (!Cv2.ImEncode(".png", copy, out var png))
                return $"{target}: PNG encoding failed";
            Directory.CreateDirectory(clipFolder);
            File.WriteAllBytes(target, png);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OpenCVException)
        {
            return $"{target}: {e.Message}";
        }
    }

    private static Rect Inflate(Rect r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static string MakeSafe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>Median of a list (the middle value of the sorted list), 0 for an empty list.</summary>
    internal static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>95th percentile by nearest rank, 0 for an empty list.</summary>
    internal static double Percentile95(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static string Format(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "~";

    /// <summary>The names of the shipped exclusion list (stars, planets, stations, ships, UNKNOWN): words on screen that are not players.</summary>
    private static IEnumerable<string> ReadExcludedNames() =>
        ExclusionList.ReadDefaultText()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>True when the folder, or one of its parents, holds a .git folder or file (the folder may not exist yet).</summary>
    public static bool IsInsideGitRepository(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        while (directory is not null)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return true;
            directory = directory.Parent;
        }

        return false;
    }
}
