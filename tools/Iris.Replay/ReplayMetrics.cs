using System.Globalization;
using System.Text;
using Iris.Core.Detection;

namespace Iris.Replay;

/// <summary>What a read name is, compared with the names that are known to be on screen.</summary>
public enum ReadKind
{
    /// <summary>One of the labeled names (exactly, or close enough: see <see cref="NameMatcher.BestMatch"/>).</summary>
    Labeled,

    /// <summary>Not a labeled name, but a word that is on screen without being a player (a planet, a ship, UNKNOWN): the exclusion list.</summary>
    Excluded,

    /// <summary>A wrong read, or a nameplate of somebody who is not in the labels, or text that is not a nameplate.</summary>
    Wrong,
}

/// <summary>The recall numbers of one clip (or one image).</summary>
public sealed class GroupMetrics
{
    public int Frames { get; internal set; }

    public int Labels { get; internal set; }

    public int Detections { get; internal set; }

    public int Wrong { get; internal set; }

    public int Excluded { get; internal set; }

    /// <summary>Distinct labeled names read exactly in at least one frame.</summary>
    public int NamesExact => ExactFrames.Count;

    /// <summary>Distinct labeled names found (exactly or fuzzy) in at least one frame.</summary>
    public int NamesFuzzy => FuzzyFrames.Count;

    /// <summary>Per labeled name: the frames where it was read exactly.</summary>
    public Dictionary<string, HashSet<int>> ExactFrames { get; } = new();

    /// <summary>Per labeled name: the frames where it was found, exactly or fuzzy.</summary>
    public Dictionary<string, HashSet<int>> FuzzyFrames { get; } = new();

    /// <summary>Number of (frame, labeled name) pairs found exactly.</summary>
    public int ExactNameFrames => ExactFrames.Values.Sum(f => f.Count);

    /// <summary>Number of (frame, labeled name) pairs found, exactly or fuzzy.</summary>
    public int FuzzyNameFrames => FuzzyFrames.Values.Sum(f => f.Count);
}

/// <summary>
/// The same measures as the Python evaluation of the training kit: for each frame, every detection is one of the labeled names
/// (read exactly, or fuzzy through <see cref="NameMatcher.BestMatch"/>), an excluded-type word (the shipped exclusion list), or a
/// wrong read. Recall is counted in "name-frames": a labeled name found in a frame counts once per frame. The labels say who appears
/// in the clip, not in which frames, so the numbers compare two runs (two detectors, two settings) rather than give an absolute rate.
/// </summary>
public sealed class ReplayMetrics
{
    private readonly ClipLabels _labels;
    private readonly string[] _excluded;
    private readonly SortedDictionary<string, GroupMetrics> _groups = new(StringComparer.Ordinal);

    public ReplayMetrics(ClipLabels labels, IEnumerable<string> excludedNames)
    {
        _labels = labels;
        _excluded = excludedNames.Select(n => n.ToUpperInvariant()).Distinct().ToArray();
    }

    public IReadOnlyDictionary<string, GroupMetrics> Groups => _groups;

    /// <summary>The key a frame is counted under: the clip for a clip list, the image file for screenshot labels.</summary>
    public string GroupKey(string clip, string frameName) => _labels.Mode == LabelMode.ByClip ? clip : frameName;

    /// <summary>The names that are known for a group, or null when the label file does not mention it.</summary>
    public IReadOnlyList<string>? LabelsOf(string key) => _labels.NamesFor(key);

    /// <summary>Classifies one read against a vocabulary (the labeled names of its clip or image).</summary>
    public ReadKind Classify(string read, IReadOnlyList<string> vocabulary, out string? label, out bool exact)
    {
        label = null;
        exact = false;
        foreach (var name in vocabulary)
        {
            if (name == read)
            {
                label = name;
                exact = true;
                return ReadKind.Labeled;
            }
        }

        var fuzzy = NameMatcher.BestMatch(read, vocabulary);
        if (fuzzy is not null)
        {
            label = fuzzy;
            return ReadKind.Labeled;
        }

        if (_excluded.Contains(read) || NameMatcher.BestMatch(read, _excluded) is not null)
            return ReadKind.Excluded;
        return ReadKind.Wrong;
    }

    /// <summary>Counts a frame (also when nothing was found in it). Frames of groups without labels are not counted.</summary>
    public void AddFrame(string clip, string frameName, int frameNumber, IReadOnlyList<NameplateDetection> detections, out IReadOnlyList<(string? Label, bool Exact)> matches)
    {
        var key = GroupKey(clip, frameName);
        var list = new List<(string?, bool)>();
        matches = list;
        var vocabulary = _labels.NamesFor(key);
        if (vocabulary is null)
            return;

        if (!_groups.TryGetValue(key, out var group))
        {
            group = new GroupMetrics { Labels = vocabulary.Count };
            _groups[key] = group;
        }

        group.Frames++;
        foreach (var detection in detections)
        {
            group.Detections++;
            var kind = Classify(detection.Name, vocabulary, out var label, out var exact);
            list.Add((label, exact));
            switch (kind)
            {
                case ReadKind.Labeled:
                    AddFrameTo(group.FuzzyFrames, label!, frameNumber);
                    if (exact)
                        AddFrameTo(group.ExactFrames, label!, frameNumber);
                    break;
                case ReadKind.Excluded:
                    group.Excluded++;
                    break;
                default:
                    group.Wrong++;
                    break;
            }
        }
    }

    private static void AddFrameTo(Dictionary<string, HashSet<int>> frames, string label, int frame)
    {
        if (!frames.TryGetValue(label, out var set))
            frames[label] = set = new HashSet<int>();
        set.Add(frame);
    }

    /// <summary>The table, with a total row; for clip lists the names that were never read exactly are listed after it.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        text.AppendLine(string.Format(inv, "{0,-24} {1,6} {2,6} | {3,9} {4,9} {5,9} {6,9} | {7,7} {8,7} {9,9}",
            _labels.Mode == LabelMode.ByClip ? "clip" : "image", "frames", "labels", "names", "exact", "fuzzy", "fz-names", "wrong", "excl.", "detections"));
        int frames = 0, labels = 0, namesExact = 0, namesFuzzy = 0, exactFrames = 0, fuzzyFrames = 0, wrong = 0, excluded = 0, detections = 0;
        foreach (var (key, g) in _groups)
        {
            text.AppendLine(string.Format(inv, "{0,-24} {1,6} {2,6} | {3,9} {4,9} {5,9} {6,9} | {7,7} {8,7} {9,9}",
                Shorten(key, 24), g.Frames, g.Labels, $"{g.NamesExact}/{g.Labels}", g.ExactNameFrames, g.FuzzyNameFrames, $"{g.NamesFuzzy}/{g.Labels}", g.Wrong, g.Excluded, g.Detections));
            frames += g.Frames;
            labels += g.Labels;
            namesExact += g.NamesExact;
            namesFuzzy += g.NamesFuzzy;
            exactFrames += g.ExactNameFrames;
            fuzzyFrames += g.FuzzyNameFrames;
            wrong += g.Wrong;
            excluded += g.Excluded;
            detections += g.Detections;
        }

        text.AppendLine(string.Format(inv, "{0,-24} {1,6} {2,6} | {3,9} {4,9} {5,9} {6,9} | {7,7} {8,7} {9,9}",
            "TOTAL", frames, labels, $"{namesExact}/{labels}", exactFrames, fuzzyFrames, $"{namesFuzzy}/{labels}", wrong, excluded, detections));
        text.AppendLine();
        text.AppendLine("names = labeled names read exactly in at least one frame; exact / fuzzy = (frame, name) pairs found exactly / exactly or close;");
        text.AppendLine("fz-names = names found exactly or close; wrong = reads that are no labeled name and no excluded-type word; excl. = excluded-type words.");

        if (_labels.Mode == LabelMode.ByClip)
        {
            var first = true;
            foreach (var (key, g) in _groups)
            {
                var missing = (_labels.NamesFor(key) ?? Array.Empty<string>())
                    .Distinct()
                    .Where(n => !g.ExactFrames.ContainsKey(n))
                    .Select(n => g.FuzzyFrames.ContainsKey(n) ? n + " (close reads only)" : n)
                    .ToList();
                if (missing.Count == 0)
                    continue;
                if (first)
                {
                    text.AppendLine();
                    text.AppendLine("Names never read exactly:");
                    first = false;
                }

                text.AppendLine($"  {key}: {string.Join(", ", missing)}");
            }
        }

        return text.ToString();
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "~";
}
