using System.Text;

namespace Iris.Replay;

/// <summary>How a label file is organized.</summary>
public enum LabelMode
{
    /// <summary>One list of names per clip (training-data/labels/clips.txt).</summary>
    ByClip,

    /// <summary>One list of names per image file (training-data/labels/screenshots.csv).</summary>
    ByFile,
}

/// <summary>
/// The names that are known to be on screen: <c>clips.txt</c> (one line per clip, <c>clip: NAME1, NAME2</c>, <c>#</c> starts a
/// comment) or <c>screenshots.csv</c> (columns <c>file,expected_players,notes</c>, names separated by <c>;</c>). Names are upper
/// case. The label files contain other players' handles: they stay on your PC, outside the repository.
/// </summary>
public sealed class ClipLabels
{
    private readonly Dictionary<string, List<string>> _names;

    private ClipLabels(LabelMode mode, Dictionary<string, List<string>> names)
    {
        Mode = mode;
        _names = names;
    }

    public LabelMode Mode { get; }

    /// <summary>The names of a clip (ByClip) or of an image file (ByFile); null when the label file does not mention it.</summary>
    public IReadOnlyList<string>? NamesFor(string key) => _names.TryGetValue(key, out var list) ? list : null;

    /// <summary>Every labeled name of every clip or image.</summary>
    public IEnumerable<string> AllNames() => _names.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal);

    public static ClipLabels Load(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ParseScreenshotCsv(text) : ParseClipList(text, path);
    }

    /// <summary>Parses clips.txt.</summary>
    public static ClipLabels ParseClipList(string text, string source = "labels")
    {
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var colon = line.IndexOf(':');
            if (colon < 0)
                throw new FormatException($"{source}:{number}: expected 'clipname: NAME1, NAME2'");
            var clip = line[..colon].Trim();
            if (!names.TryGetValue(clip, out var list))
                names[clip] = list = new List<string>();
            foreach (var name in line[(colon + 1)..].Split(',', ';'))
            {
                var trimmed = name.Trim().ToUpperInvariant();
                if (trimmed.Length > 0)
                    list.Add(trimmed);
            }
        }

        return new ClipLabels(LabelMode.ByClip, names);
    }

    /// <summary>Parses screenshots.csv: a header, then <c>file,expected_players,notes</c> (fields may be quoted).</summary>
    public static ClipLabels ParseScreenshotCsv(string text)
    {
        var rows = ReadCsv(text);
        if (rows.Count == 0)
            throw new FormatException("The label file is empty.");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var fileColumn = header.IndexOf("file");
        var namesColumn = header.IndexOf("expected_players");
        if (fileColumn < 0 || namesColumn < 0)
            throw new FormatException("The label file needs the columns file and expected_players.");

        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(1))
        {
            if (row.Count <= fileColumn || row[fileColumn].Trim().Length == 0)
                continue;
            var list = new List<string>();
            if (row.Count > namesColumn)
            {
                foreach (var name in row[namesColumn].Split(';'))
                {
                    var trimmed = name.Trim().ToUpperInvariant();
                    if (trimmed.Length > 0)
                        list.Add(trimmed);
                }
            }

            names[row[fileColumn].Trim()] = list;
        }

        return new ClipLabels(LabelMode.ByFile, names);
    }

    /// <summary>A small CSV reader: commas, double quoted fields ("" is a quote), line breaks inside quotes.</summary>
    internal static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                    rows.Add(row);
                row = new List<string>();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
