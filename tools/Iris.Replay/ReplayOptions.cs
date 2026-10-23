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

          <input>      a video file (mp4, mkv, mov, avi, webm), a folder of frame images (png, jpg, bmp, tif),
                       a folder with one such sub folder (or video) per clip, or a single image
          --labels F   clip labels (clips.txt: "clip: NAME1, NAME2"), or screenshot labels (a .csv with the columns
                       file,expected_players,... and ';' between the names): reports recall and wrong reads
          --out FOLDER where detections.csv and the annotated images go (default: Iris\replay in the local application data
                       folder). It must not be inside a git repository: frames and reads are your own data
          --annotate   also write a PNG per frame with a nameplate, with boxes and reads, to FOLDER\annotated
          --every N    use every Nth frame (default 1)
          --model F    use this letters-model.bin instead of the one built into IRIS
          --region R   scan region as fractions of the frame: left,right,top,bottom (default 0.15,0.85,0.10,0.70)

        If a video does not open, cut it into frames first (ffmpeg -i clip.mp4 -vf fps=10 frames\%05d.jpg) and give the folder.
        """;

    public string Input { get; private set; } = string.Empty;

    public string? LabelsPath { get; private set; }

    public string OutDirectory { get; private set; } = DefaultOutDirectory();

    public bool Annotate { get; private set; }

    public int Every { get; private set; } = 1;

    public string? ModelPath { get; private set; }

    public NameplateDetectorSettings Settings { get; private set; } = NameplateDetectorSettings.Default;

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
                case "--labels":
                case "--out":
                case "--model":
                case "--every":
                case "--region":
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
