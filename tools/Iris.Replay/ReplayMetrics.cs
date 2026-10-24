using System.Globalization;
using System.Text;
using Iris.Core.Detection;

namespace Iris.Replay;

/// <summary>What a read or confirmed name is, compared with the names that are known to be on screen.</summary>
public enum ReadKind
{
    /// <summary>One of the labeled names (exactly, ignoring only upper/lower case: see <see cref="NameMatcher"/>; there is no look-alike tolerance).</summary>
    Labeled,

    /// <summary>
    /// Not a labeled name, but one the app ignores by name: a word of the exclusion list (stars, planets, ships, UNKNOWN; exact) or a
    /// contact code such as <c>PA-5968-XF</c> (see <see cref="NameFilter"/>).
    /// </summary>
    Excluded,

    /// <summary>A wrong read, or a nameplate of somebody who is not in the labels, or text that is not a nameplate.</summary>
    Wrong,
}

/// <summary>A labeled name that a track confirmed.</summary>
/// <param name="Label">The labeled name (the confirmed text was identical to it).</param>
/// <param name="ConfirmedFrame">Frame number at which it was confirmed.</param>
/// <param name="DelayFrames">Frames from the first read of this name by its track to its confirmation.</param>
/// <param name="DelayMs">The same in milliseconds of video time.</param>
public sealed record FoundName(string Label, long ConfirmedFrame, long DelayFrames, double DelayMs);

/// <summary>The measures of one clip (or one image).</summary>
public sealed class GroupMetrics
{
    public int Frames { get; internal set; }

    public int Labels { get; internal set; }

    /// <summary>Reads (one per nameplate evaluation that read a sure enough name) that are labeled / wrong / excluded-type.</summary>
    public int Reads { get; internal set; }

    public int WrongReads { get; internal set; }

    public int ExcludedReads { get; internal set; }

    /// <summary>Tracks that were a nameplate at some time (they passed the distance anchor).</summary>
    public int NameplateTracks { get; internal set; }

    /// <summary>Nameplate tracks whose best read is none of: a labeled name, an excluded name, a contact code, a stage of an animation.</summary>
    public int JunkTracks { get; internal set; }

    /// <summary>Nameplate tracks whose best read is a prefix or near miss of a known name (the game types or scrambles a name when a plate appears).</summary>
    public int AnimationTracks { get; internal set; }

    /// <summary>Nameplate tracks that never read a name surely enough (nothing is shown for them).</summary>
    public int UnreadTracks { get; internal set; }

    /// <summary>Nameplate tracks of excluded names and contact codes.</summary>
    public int FilteredTracks { get; internal set; }

    /// <summary>How often a confirmed name was CORRECTED (switched to another name) by the tracks of the clip.</summary>
    public int Corrections { get; internal set; }

    /// <summary>Per labeled name: the first time a track confirmed it.</summary>
    public Dictionary<string, FoundName> Found { get; } = new();

    /// <summary>Confirmed names that are no labeled name, no excluded name, no contact code: the app would look them up.</summary>
    public SortedSet<string> WrongConfirmed { get; } = new(StringComparer.Ordinal);

    /// <summary>The wrong names among the names the tracks ended with (their confirmed name when they closed): what is still shown after all corrections.</summary>
    public SortedSet<string> WrongFinal { get; } = new(StringComparer.Ordinal);

    /// <summary>Confirmed names that are not labeled but are ignored by name (exclusion list, contact codes).</summary>
    public SortedSet<string> FilteredConfirmed { get; } = new(StringComparer.Ordinal);

    /// <summary>Milliseconds per frame (cheap pass, reads and tracker).</summary>
    public List<double> FrameMs { get; } = new();

    /// <summary>Single image mode: per labeled name, the frames where it was read (exactly).</summary>
    public Dictionary<string, HashSet<int>> ReadFrames { get; } = new();

    /// <summary>Labeled names confirmed (or, in single image mode, read) at least once.</summary>
    public int NamesFound => Found.Count + ReadFrames.Count;

    public double? MedianDelayFrames => Found.Count == 0 ? null : Program.Median(Found.Values.Select(f => (double)f.DelayFrames).ToList());

    public double? MedianDelayMs => Found.Count == 0 ? null : Program.Median(Found.Values.Select(f => f.DelayMs).ToList());
}

/// <summary>
/// The measures of the Python evaluation of the training kit, per clip, computed from tracks: a labeled name is FOUND when a track
/// confirmed it (read identically several times). Wrong CONFIRMED names are the ones that would be matched and looked up (target: none).
/// The labels say who appears in the clip, not in which frames, so the numbers compare two runs (two detectors, two settings) rather than
/// give an absolute rate.
/// </summary>
public sealed class ReplayMetrics
{
    private readonly ClipLabels _labels;
    private readonly HashSet<string> _excluded;
    private readonly string[] _known;
    private readonly SortedDictionary<string, GroupMetrics> _groups = new(StringComparer.Ordinal);

    public ReplayMetrics(ClipLabels labels, IEnumerable<string> excludedNames)
    {
        _labels = labels;
        var excluded = excludedNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        _excluded = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        _known = labels.AllNames().Concat(excluded).Select(n => n.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyDictionary<string, GroupMetrics> Groups => _groups;

    /// <summary>The key a frame is counted under: the clip for a clip list, the image file for screenshot labels.</summary>
    public string GroupKey(string clip, string frameName) => _labels.Mode == LabelMode.ByClip ? clip : frameName;

    /// <summary>The names that are known for a group, or null when the label file does not mention it.</summary>
    public IReadOnlyList<string>? LabelsOf(string key) => _labels.NamesFor(key);

    /// <summary>
    /// Classifies one read or confirmed name against a vocabulary (the labeled names of its clip or image). Names match exactly (ignoring only
    /// upper/lower case): a read with a 0 for an O is a wrong read, not a found name.
    /// </summary>
    public ReadKind Classify(string read, IReadOnlyList<string> vocabulary, out string? label)
    {
        label = NameMatcher.BestMatch(read, vocabulary);
        if (label is not null)
            return ReadKind.Labeled;
        if (_excluded.Contains(read) || ContactCode.IsContactCode(read))
            return ReadKind.Excluded;
        return ReadKind.Wrong;
    }

    private GroupMetrics? Group(string key)
    {
        var vocabulary = _labels.NamesFor(key);
        if (vocabulary is null)
            return null;
        if (!_groups.TryGetValue(key, out var group))
        {
            group = new GroupMetrics { Labels = vocabulary.Distinct().Count() };
            _groups[key] = group;
        }

        return group;
    }

    /// <summary>Counts a frame and its time. Frames of groups without labels are not counted.</summary>
    public void AddFrame(string clip, string frameName, double milliseconds)
    {
        var group = Group(GroupKey(clip, frameName));
        if (group is null)
            return;
        group.Frames++;
        group.FrameMs.Add(milliseconds);
    }

    /// <summary>
    /// Counts the reads of one frame (also for the "match" columns of the csv). In single image mode (screenshot labels) these reads are
    /// what is measured: names read exactly, and wrong reads.
    /// </summary>
    public void AddReads(string clip, string frameName, int frameNumber, IReadOnlyList<NameplateDetection> detections, out IReadOnlyList<string?> matches)
    {
        var list = new List<string?>();
        matches = list;
        var key = GroupKey(clip, frameName);
        var vocabulary = _labels.NamesFor(key);
        var group = Group(key);
        if (vocabulary is null || group is null)
            return;

        foreach (var detection in detections)
        {
            group.Reads++;
            var kind = Classify(detection.Name, vocabulary, out var label);
            list.Add(label);
            switch (kind)
            {
                case ReadKind.Labeled:
                    AddFrameTo(group.ReadFrames, label!, frameNumber);
                    break;
                case ReadKind.Excluded:
                    group.ExcludedReads++;
                    break;
                default:
                    group.WrongReads++;
                    break;
            }
        }
    }

    /// <summary>Counts a track that is finished (expired, or the clip ended): what it confirmed and what kind of track it was.</summary>
    public void AddTrack(string clip, NameplateTrack track)
    {
        var key = GroupKey(clip, string.Empty);
        var vocabulary = _labels.NamesFor(key);
        var group = Group(key);
        if (vocabulary is null || group is null)
            return;

        group.Corrections += track.Corrections.Count;
        if (track.ConfirmedName is not null && Classify(track.ConfirmedName, vocabulary, out _) == ReadKind.Wrong)
            group.WrongFinal.Add(track.ConfirmedName);
        foreach (var (name, frame, ms) in track.Confirmations)
        {
            switch (Classify(name, vocabulary, out var label))
            {
                case ReadKind.Labeled:
                    var first = track.FirstReadFrame.TryGetValue(name, out var f) ? f : frame;
                    var firstMs = track.Reads.FirstOrDefault(r => r.Name == name)?.FirstMs ?? ms;
                    var found = new FoundName(label!, frame, frame - first, ms - firstMs);
                    if (!group.Found.TryGetValue(label!, out var known) || frame < known.ConfirmedFrame)
                        group.Found[label!] = found;
                    break;
                case ReadKind.Excluded:
                    group.FilteredConfirmed.Add(name);
                    break;
                default:
                    group.WrongConfirmed.Add(name);
                    break;
            }
        }

        if (!track.EverNameplate)
            return;
        group.NameplateTracks++;
        var best = track.BestName;
        if (best is null)
        {
            group.UnreadTracks++;
            return;
        }

        switch (Classify(best, vocabulary, out _))
        {
            case ReadKind.Labeled:
                break;
            case ReadKind.Excluded:
                group.FilteredTracks++;
                break;
            default:
                if (SimilarToKnown(best))
                    group.AnimationTracks++;
                else
                    group.JunkTracks++;
                break;
        }
    }

    /// <summary>
    /// A read that is a stage of a nameplate animation or a near miss of a known name: a prefix (3 or more letters) of a known name
    /// (labeled anywhere, or excluded), or within max(2, 30% of the length) edits of one.
    /// </summary>
    public bool SimilarToKnown(string read)
    {
        var folded = read.ToUpperInvariant();
        foreach (var known in _known)
        {
            if (folded.Length >= 3 && known.StartsWith(folded, StringComparison.Ordinal))
                return true;
            if (EditDistance(folded, known) <= Math.Max(2.0, 0.3 * known.Length))
                return true;
        }

        return false;
    }

    internal static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static void AddFrameTo(Dictionary<string, HashSet<int>> frames, string label, int frame)
    {
        if (!frames.TryGetValue(label, out var set))
            frames[label] = set = new HashSet<int>();
        set.Add(frame);
    }

    /// <summary>The table with a total row, then the names that were not confirmed, the wrong names that were confirmed, and the ignored ones.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        if (_labels.Mode == LabelMode.ByFile)
        {
            FormatImages(text, inv);
            return text.ToString();
        }

        const string row = "{0,-24} {1,6} {2,6} | {3,9} | {4,8} {5,8} | {6,6} {7,8} {8,5} | {9,5} {10,5} {11,6} {12,5} | {13,7} {14,7}";
        text.AppendLine(string.Format(inv, row, "clip", "frames", "labels", "found", "delay fr", "delay ms", "wrong", "wrongEnd", "corr", "junk", "anim", "unread", "ignrd", "ms med", "ms p95"));
        int frames = 0, labels = 0, found = 0, wrong = 0, wrongEnd = 0, corrections = 0, junk = 0, anim = 0, unread = 0, filtered = 0;
        var delaysFrames = new List<double>();
        var delaysMs = new List<double>();
        var allMs = new List<double>();
        foreach (var (key, g) in _groups)
        {
            text.AppendLine(string.Format(inv, row, Shorten(key, 24), g.Frames, g.Labels, $"{g.Found.Count}/{g.Labels}",
                Number(g.MedianDelayFrames), Number(g.MedianDelayMs), g.WrongConfirmed.Count, g.WrongFinal.Count, g.Corrections, g.JunkTracks, g.AnimationTracks,
                g.UnreadTracks, g.FilteredTracks, Number(Program.Median(g.FrameMs)), Number(Program.Percentile95(g.FrameMs))));
            frames += g.Frames;
            labels += g.Labels;
            found += g.Found.Count;
            wrong += g.WrongConfirmed.Count;
            wrongEnd += g.WrongFinal.Count;
            corrections += g.Corrections;
            junk += g.JunkTracks;
            anim += g.AnimationTracks;
            unread += g.UnreadTracks;
            filtered += g.FilteredTracks;
            delaysFrames.AddRange(g.Found.Values.Select(x => (double)x.DelayFrames));
            delaysMs.AddRange(g.Found.Values.Select(x => x.DelayMs));
            allMs.AddRange(g.FrameMs);
        }

        text.AppendLine(string.Format(inv, row, "TOTAL", frames, labels, $"{found}/{labels}",
            Number(delaysFrames.Count == 0 ? null : Program.Median(delaysFrames)), Number(delaysMs.Count == 0 ? null : Program.Median(delaysMs)),
            wrong, wrongEnd, corrections, junk, anim, unread, filtered, Number(Program.Median(allMs)), Number(Program.Percentile95(allMs))));
        text.AppendLine();
        text.AppendLine("found = labeled names a track CONFIRMED (read identically several times, with exactly the labeled spelling: names match exactly, 0/O and 1/I are different);");
        text.AppendLine("delay = median, over the found names, from the first read of the name to its confirmation (frames and ms of video time);");
        text.AppendLine("wrong = names CONFIRMED that are not labeled, not excluded, not contact codes: the app would look them up (the target is 0); wrongEnd = the same for the name each track ended with, after corrections;");
        text.AppendLine("corr = confirmed names that were CORRECTED (the track switched to another name that it read more often over the recent window);");
        text.AppendLine("junk / anim / unread = nameplate tracks whose best read is none of the known names / a stage or near miss of one / nothing readable; ignrd = tracks of excluded names and contact codes.");

        var first = true;
        foreach (var (key, g) in _groups)
        {
            var missing = (_labels.NamesFor(key) ?? Array.Empty<string>()).Distinct().Where(n => !g.Found.ContainsKey(n)).ToList();
            if (missing.Count == 0)
                continue;
            if (first)
            {
                text.AppendLine();
                text.AppendLine("Labeled names that no track confirmed:");
                first = false;
            }

            text.AppendLine($"  {key}: {string.Join(", ", missing)}");
        }

        AppendNameLists(text, "WRONG names that were confirmed (they would be looked up):", g => g.WrongConfirmed);
        AppendNameLists(text, "Confirmed but ignored by name (exclusion list or contact code), not labeled:", g => g.FilteredConfirmed);
        return text.ToString();
    }

    private void AppendNameLists(StringBuilder text, string title, Func<GroupMetrics, IEnumerable<string>> pick)
    {
        var first = true;
        foreach (var (key, g) in _groups)
        {
            var names = pick(g).ToList();
            if (names.Count == 0)
                continue;
            if (first)
            {
                text.AppendLine();
                text.AppendLine(title);
                first = false;
            }

            text.AppendLine($"  {key}: {string.Join(", ", names)}");
        }
    }

    private void FormatImages(StringBuilder text, IFormatProvider inv)
    {
        const string row = "{0,-24} {1,6} | {2,9} | {3,7} {4,7} {5,7}";
        text.AppendLine(string.Format(inv, row, "image", "labels", "read", "wrong", "excl.", "reads"));
        int labels = 0, read = 0, wrong = 0, excluded = 0, reads = 0;
        foreach (var (key, g) in _groups)
        {
            text.AppendLine(string.Format(inv, row, Shorten(key, 24), g.Labels, $"{g.ReadFrames.Count}/{g.Labels}", g.WrongReads, g.ExcludedReads, g.Reads));
            labels += g.Labels;
            read += g.ReadFrames.Count;
            wrong += g.WrongReads;
            excluded += g.ExcludedReads;
            reads += g.Reads;
        }

        text.AppendLine(string.Format(inv, row, "TOTAL", labels, $"{read}/{labels}", wrong, excluded, reads));
        text.AppendLine();
        text.AppendLine("Every image is read once, without the tracker. read = labeled names read exactly (0/O and 1/I are different);");
        text.AppendLine("wrong = reads that are no labeled name, no excluded name, no contact code; excl. = excluded names and contact codes.");
    }

    private static string Number(double? value) => value is null ? "-" : value.Value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "~";
}
