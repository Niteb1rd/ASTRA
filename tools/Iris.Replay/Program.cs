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
    private const string CsvHeader = "source,frame,track,verdict,confirmed,name,score,x,y,w,h,distance,dist_x,dist_y,dist_w,dist_h,match,exact";
    private const string TrackHeader = "clip,track,first_frame,first_ms,last_seen_ms,ever_nameplate,evaluations,best_name,confirmed_names,reads";

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

    private sealed record ClipSummary(string Clip, int Frames, int NameplateTracks, int Confirmed, List<double> Times);

    private static int Run(ReplayOptions options)
    {
        if (IsInsideGitRepository(options.OutDirectory))
        {
            Console.Error.WriteLine($"Refusing to write into {Path.GetFullPath(options.OutDirectory)}: it is inside a git repository. Detections and annotated frames contain other players' handles and game imagery; choose another --out folder.");
            return 2;
        }

        ClipLabels? labels = options.LabelsPath is null ? null : ClipLabels.Load(options.LabelsPath);
        var single = labels?.Mode == LabelMode.ByFile;
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
        var tracksPath = Path.Combine(options.OutDirectory, "tracks.csv");
        using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
        using var tracksCsv = new StreamWriter(tracksPath, false, new UTF8Encoding(false));
        csv.WriteLine(CsvHeader);
        tracksCsv.WriteLine(TrackHeader);

        var inv = CultureInfo.InvariantCulture;
        var step = options.Every == 1 ? "every frame" : string.Create(inv, $"every {options.Every} frames");
        var settings = options.Settings;
        var mode = single ? "single images, no tracker" : $"tracker: {options.TrackerSettings.ConfirmReads} identical reads over at least {options.TrackerSettings.ConfirmSpanMs:0} ms";
        Console.WriteLine(string.Create(inv, $"Replaying {clips.Count} clip(s), {step}, scan region x {settings.RegionLeft:P0}-{settings.RegionRight:P0}, y {settings.RegionTop:P0}-{settings.RegionBottom:P0}; {mode}."));
        Console.WriteLine();

        var summaries = new List<ClipSummary>();
        foreach (var clip in clips)
        {
            var times = new List<double>();
            var unreadable = 0;
            var annotateFailures = new List<string>();
            string? skipped = null;
            var tracker = new NameplateTracker(options.TrackerSettings);
            var nameplateTracks = 0;
            var confirmedNames = new HashSet<string>();

            void CloseTracks(IEnumerable<NameplateTrack> closed)
            {
                foreach (var track in closed)
                {
                    if (track.EverNameplate)
                        nameplateTracks++;
                    foreach (var c in track.Confirmations)
                        confirmedNames.Add(c.Name);
                    metrics?.AddTrack(clip.Clip, track);
                    tracksCsv.WriteLine(TrackRow(clip.Clip, track));
                }
            }

            try
            {
                foreach (var frame in FrameSources.Frames(clip, options.Every, _ => unreadable++, options.ImageFps))
                {
                    using var image = frame.Image;
                    if (single)
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var detections = detector.Detect(image);
                        stopwatch.Stop();
                        times.Add(stopwatch.Elapsed.TotalMilliseconds);
                        metrics!.AddFrame(frame.Clip, frame.Name, stopwatch.Elapsed.TotalMilliseconds);
                        metrics.AddReads(frame.Clip, frame.Name, frame.Number, detections, out var matches);
                        for (var i = 0; i < detections.Count; i++)
                        {
                            var match = i < matches.Count ? matches[i] : (Label: (string?)null, Exact: false);
                            csv.WriteLine(CsvRow(clip.Clip, frame, null, detections[i], match.Label, match.Exact, true));
                        }

                        if (options.Annotate && detections.Count > 0)
                        {
                            var shapes = detections.Select(d => new Marked(d.NameBox, d.DistanceBox, $"{d.Name} {d.MeanScore:0.00}", Green)).ToList();
                            var problem = SaveAnnotated(image, shapes, annotatedFolder, clip, frame);
                            if (problem is not null)
                                annotateFailures.Add(problem);
                        }

                        continue;
                    }

                    var watch = Stopwatch.StartNew();
                    var candidates = detector.FindCandidates(image);
                    var evaluator = detector.CreateEvaluator(image, candidates);
                    var list = evaluator.TrackCandidates();
                    var evaluated = new Dictionary<Rect, PairEvaluation>();
                    var update = tracker.Update(frame.Number, frame.TimeMs, list, (top, bottom) =>
                    {
                        var evaluation = evaluator.Evaluate(top, bottom);
                        evaluated[top] = evaluation;
                        return new TrackEvaluation(evaluation.IsNameplate, evaluation.Detection?.Name);
                    });
                    watch.Stop();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    metrics?.AddFrame(frame.Clip, frame.Name, watch.Elapsed.TotalMilliseconds);
                    CloseTracks(update.Closed);

                    var marks = new List<Marked>();
                    foreach (var track in tracker.Tracks)
                    {
                        if (track.LastSeenMs != frame.TimeMs)
                            continue;
                        evaluated.TryGetValue(track.Top, out var evaluation);
                        var confirmed = track.ConfirmedName is not null;
                        if (evaluation?.Detection is not null)
                        {
                            var d = evaluation.Detection;
                            csv.WriteLine(CsvRow(clip.Clip, frame, track, d, null, false, false));
                        }

                        if (confirmed)
                            marks.Add(new Marked(track.Top, track.Bottom, track.ConfirmedName!, Green));
                        else if (track.Verdict == TrackVerdict.Nameplate)
                            marks.Add(new Marked(track.Top, track.Bottom, evaluation?.Detection?.Name ?? track.BestName ?? "?", Yellow));
                        else if (evaluation is not null)
                            marks.Add(new Marked(track.Top, track.Bottom, string.Empty, Red));
                    }

                    if (options.Annotate && marks.Count > 0)
                    {
                        var problem = SaveAnnotated(image, marks, annotatedFolder, clip, frame);
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

            if (!single)
                CloseTracks(tracker.Finish());
            summaries.Add(new ClipSummary(clip.Clip, times.Count, nameplateTracks, confirmedNames.Count, times));
            if (skipped is not null)
                Console.WriteLine($"{clip.Clip}: skipped: {skipped}");
            if (unreadable > 0)
                Console.WriteLine($"{clip.Clip}: {unreadable} image file(s) could not be read and were skipped");
            if (annotateFailures.Count > 0)
                Console.WriteLine($"{clip.Clip}: {annotateFailures.Count} annotated image(s) could not be written, the first: {annotateFailures[0]}");
        }

        csv.Flush();
        tracksCsv.Flush();
        if (metrics is null)
        {
            Console.WriteLine(string.Format(inv, "{0,-24} {1,7} {2,16} {3,10} {4,10} {5,9}", "clip", "frames", "nameplate tracks", "confirmed", "ms median", "ms p95"));
            var all = new List<double>();
            foreach (var s in summaries)
            {
                Console.WriteLine(string.Format(inv, "{0,-24} {1,7} {2,16} {3,10} {4,10} {5,9}", Shorten(s.Clip, 24), s.Frames, s.NameplateTracks, s.Confirmed, Format(Median(s.Times)), Format(Percentile95(s.Times))));
                all.AddRange(s.Times);
            }

            Console.WriteLine(string.Format(inv, "{0,-24} {1,7} {2,16} {3,10} {4,10} {5,9}", "ALL", all.Count, string.Empty, string.Empty, Format(Median(all)), Format(Percentile95(all))));
        }

        Console.WriteLine();
        Console.WriteLine($"Detections: {csvPath}");
        Console.WriteLine($"Tracks: {tracksPath}");
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

    private static readonly Scalar Green = new(0, 255, 0);
    private static readonly Scalar Yellow = new(0, 220, 255);
    private static readonly Scalar Red = new(0, 0, 255);

    private sealed record Marked(Rect Top, Rect Bottom, string Label, Scalar Color);

    private static string TrackRow(string clip, NameplateTrack track)
    {
        var inv = CultureInfo.InvariantCulture;
        var reads = string.Join('|', track.Reads.OrderByDescending(r => r.Count).Select(r => $"{r.Name}:{r.Count}"));
        var confirmed = string.Join('|', track.Confirmations.Select(c => c.Name).Distinct());
        return string.Join(',',
            Escape(clip), track.Id.ToString(inv), track.FirstFrame.ToString(inv), track.FirstMs.ToString("0", inv), track.LastSeenMs.ToString("0", inv),
            track.EverNameplate ? "1" : "0", track.Evaluations.ToString(inv), Escape(track.BestName ?? string.Empty), Escape(confirmed), Escape(reads));
    }

    private static string CsvRow(string clip, SourceFrame frame, NameplateTrack? track, NameplateDetection d, string? label, bool exact, bool labeled)
    {
        var inv = CultureInfo.InvariantCulture;
        // a video frame (clip#number) and a single image (named like its clip) stand alone; an image in a folder is folder/file
        var source = frame.Name.Contains('#') || Path.GetFileNameWithoutExtension(frame.Name) == clip ? frame.Name : $"{clip}/{frame.Name}";
        var fields = new[]
        {
            Escape(source),
            frame.Number.ToString(inv),
            track is null ? string.Empty : track.Id.ToString(inv),
            track is null ? "Single" : track.Verdict.ToString(),
            track is null ? string.Empty : Escape(track.ConfirmedName ?? string.Empty),
            Escape(d.Name),
            d.Score.ToString("0.0000", inv),
            d.NameBox.X.ToString(inv), d.NameBox.Y.ToString(inv), d.NameBox.Width.ToString(inv), d.NameBox.Height.ToString(inv),
            Escape(d.DistanceText),
            d.DistanceBox.X.ToString(inv), d.DistanceBox.Y.ToString(inv), d.DistanceBox.Width.ToString(inv), d.DistanceBox.Height.ToString(inv),
            labeled ? Escape(label ?? string.Empty) : string.Empty,
            labeled ? (exact ? "1" : "0") : string.Empty,
        };
        return string.Join(',', fields);
    }

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>Writes the annotated PNG. Returns null, or why it could not be written.</summary>
    private static string? SaveAnnotated(Mat image, IReadOnlyList<Marked> marks, string folder, ClipInput clip, SourceFrame frame)
    {
        using var copy = image.Clone();
        var thickness = Math.Max(1, image.Height / 700);
        var fontScale = Math.Max(0.4, image.Height / 1800.0);
        foreach (var m in marks)
        {
            Cv2.Rectangle(copy, Inflate(m.Top, 2), m.Color, thickness);
            Cv2.Rectangle(copy, Inflate(m.Bottom, 2), m.Color, thickness);
            if (m.Label.Length > 0)
                Cv2.PutText(copy, m.Label, new Point(m.Top.X, Math.Max(14, m.Top.Y - 6)), HersheyFonts.HersheySimplex, fontScale, m.Color, thickness);
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
