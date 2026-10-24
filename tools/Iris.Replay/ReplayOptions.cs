using System.Globalization;
using Iris.Core.Detection;

namespace Iris.Replay;

/// <summary>The command line of Iris.Replay.</summary>
public sealed class ReplayOptions
{
    public const string Usage = """
        Iris.Replay: run the IRIS nameplate detector over recorded frames.

        Usage:
          Iris.Replay <input> [--labels FILE] [--out FOLDER] [--annotate] [--every N] [--model FILE] [--region L,R,T,B]
                              [--fps N] [--image-fps N] [--confirm-reads N] [--confirm-span MS] [--track]

          <input>      a video file (mp4, mkv, mov, avi, webm), a folder of frame images (png, jpg, bmp, tif),
                       a folder with one such sub folder (or video) per clip, or a single image
          --labels F   clip labels (clips.txt: "clip: NAME1, NAME2"), or screenshot labels (a .csv with the columns
                       file,expected_players,... and ';' between the names): reports recall and wrong reads.
                       A name wrapped over several rows is written row by row with '/' (FIRSTROW/SECONDROW); a contact code
                       (PA-5968-XF) counts as found when read and is not a miss when it is not
          --out FOLDER where detections.csv and the annotated images go (default: Iris\replay in the local application data
                       folder). It must not be inside a git repository: frames and reads are your own data
          --annotate   also write a PNG per frame with tracked lines to FOLDER\annotated: confirmed names green, nameplates not yet
                       confirmed yellow, rejected pairs red
          --every N    use every Nth frame (default 1)
          --fps N      sample the frames by TIME: keep a frame when its timestamp has advanced at least 1000/N ms since the last kept
                       one (default: every frame). A video of 60 or 120 fps is read much more often than the app will read (5 times a
                       second, planned), which lets a short-lived misread reach the confirmation; --fps 5 mirrors that rate. Applied after --every.
                       Frame numbers (the frame column, first_frame, delay fr) stay the NATIVE video frame numbers, not a count of the
                       sampled frames; delay ms is the figure to compare with the app. Ignored for single images
          --model F    use this letters-model.bin instead of the one built into IRIS
          --region R   scan region as fractions of the frame: left,right,top,bottom (default 0.15,0.85,0.10,0.70)
          --image-fps N   frame rate that the frame numbers of image files stand for (default 10, as in the training kit); a video
                       brings its own (this was --fps before --fps became the sampling rate)
          --confirm-reads N   a name is confirmed after N identical reads (default 3)
          --confirm-span MS   ... spread over at least this many milliseconds (default 300)
          --track      simulate the app's detect-then-track instead of reading every sampled frame: the detector runs at --fps (default 5 a
                       second) and gives the template tracker the name rows' text, position and speed of every confirmed plate; on all the other
                       frames, at the video's own frame rate, the tracker follows the plates by template matching. Reports, per clip, the
                       distance (median and 95th percentile, pixels) between the tracked bracket and the fresh detection of the same plate at each
                       detection, the share of frames with a tracked bracket shown (between the first and the last detection that saw a plate),
                       the share of plates still shown at the next detection, the share of template matches found and the cost of a tracking
                       tick; the comparisons go to tracking.csv in FOLDER. --labels and --annotate are ignored

        Frames of a clip are run through the tracker in order. With screenshot labels (--labels file.csv) every image stands alone and
        is read once, without the tracker.

        If a video does not open, cut it into frames first (ffmpeg -i clip.mp4 -vf fps=10 frames\%05d.jpg) and give the folder.
        """;

    public string Input { get; private set; } = string.Empty;

    public string? LabelsPath { get; private set; }

    public string OutDirectory { get; private set; } = DefaultOutDirectory();

    public bool Annotate { get; private set; }

    public int Every { get; private set; } = 1;

    public string? ModelPath { get; private set; }

    public NameplateDetectorSettings Settings { get; private set; } = NameplateDetectorSettings.Default;

    public NameplateTrackerSettings TrackerSettings { get; private set; } = NameplateTrackerSettings.Default;

    /// <summary>Frame rate that the frame numbers of image files stand for (<c>--image-fps</c>).</summary>
    public double ImageFps { get; private set; } = 10.0;

    /// <summary>Sampling rate in frames a second (<c>--fps</c>): frames are kept by time, 1000/N ms apart at least. Null keeps every frame.</summary>
    public double? SampleFps { get; private set; }

    /// <summary>Simulate detect-then-track (<c>--track</c>) instead of the plain replay.</summary>
    public bool Track { get; private set; }

    public bool ShowHelp { get; private set; }

    public static string DefaultOutDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "replay");

    /// <summary>Parses the arguments. Returns the options, or null and the reason in <paramref name="error"/>.</summary>
    public static ReplayOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var options = new ReplayOptions();
        string? input = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                case "/?":
                    options.ShowHelp = true;
                    return options;
                case "--annotate":
                    options.Annotate = true;
                    break;
                case "--track":
                    options.Track = true;
                    break;
                case "--labels":
                case "--out":
                case "--model":
                case "--every":
                case "--region":
                case "--fps":
                case "--image-fps":
                case "--confirm-reads":
                case "--confirm-span":
                    if (i + 1 >= args.Count)
                    {
                        error = $"{arg} needs a value.";
                        return null;
                    }

                    i++;
                    error = options.Apply(arg, args[i]);
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        error = $"Unknown option {arg}.";
                    else if (input is not null)
                        error = $"More than one input ({input} and {arg}).";
                    else
                        input = arg;
                    break;
            }

            if (error is not null)
                return null;
        }

        if (input is null)
        {
            error = "No input given.";
            return null;
        }

        options.Input = input;
        return options;
    }

    /// <summary>Applies one option that has a value; returns the problem, or null.</summary>
    private string? Apply(string name, string value)
    {
        switch (name)
        {
            case "--labels":
                LabelsPath = value;
                return null;
            case "--out":
                OutDirectory = value;
                return null;
            case "--model":
                ModelPath = value;
                return null;
            case "--every":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var every) || every < 1)
                    return "--every needs a whole number of at least 1.";
                Every = every;
                return null;
            case "--fps":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || !(fps > 0 && fps <= 1000))
                    return "--fps needs a number above 0 (frames a second to sample, such as 5).";
                SampleFps = fps;
                return null;
            case "--image-fps":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var imageFps) || !(imageFps > 0 && imageFps <= 1000))
                    return "--image-fps needs a number above 0.";
                ImageFps = imageFps;
                return null;
            case "--confirm-reads":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reads) || reads < 1)
                    return "--confirm-reads needs a whole number of at least 1.";
                TrackerSettings = TrackerSettings with { ConfirmReads = reads };
                return null;
            case "--confirm-span":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var span) || span < 0)
                    return "--confirm-span needs a number of milliseconds, 0 or more.";
                TrackerSettings = TrackerSettings with { ConfirmSpanMs = span };
                return null;
            default: // --region
                var changed = ParseRegion(value, Settings, out var problem);
                Settings = changed;
                return problem;
        }
    }

    private static NameplateDetectorSettings ParseRegion(string text, NameplateDetectorSettings settings, out string? error)
    {
        error = null;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var values = new double[4];
        if (parts.Length != 4 || parts.Where((p, k) => !double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out values[k])).Any())
        {
            error = "--region needs four numbers: left,right,top,bottom (for example 0.15,0.85,0.10,0.70).";
            return settings;
        }

        var changed = settings with { RegionLeft = values[0], RegionRight = values[1], RegionTop = values[2], RegionBottom = values[3] };
        error = changed.Validate();
        return error is null ? changed : settings;
    }
}
