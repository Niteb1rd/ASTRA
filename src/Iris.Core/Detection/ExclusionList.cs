using System.Reflection;
using System.Text;
using Iris.Core.Config;
using Iris.Core.Storage;

namespace Iris.Core.Detection;

/// <summary>
/// The exclusion list: names that show up on screen but are not players (stars, planets, stations, ship names...).
/// A nameplate with one of these names is never matched or looked up (see <see cref="NameFilter"/>).
///
/// The list lives in a plain text file, <c>exclusions.txt</c>, that the user can edit by hand:
/// one name per line, matched without regard to upper or lower case; blank lines are ignored;
/// a line starting with # is a comment. The first time IRIS runs, the file is created from the list that ships inside
/// the program (<c>exclusions.default.txt</c>). Where the file lives is decided by the app layer.
///
/// IRIS itself never writes to the file after creating it: the duplicate rule (a name on 2+ white nameplates) only excludes names for the running session
/// (<see cref="SessionExclusions"/>, in memory), and the file holds just the user's own entries and the shipped defaults.
/// Call <see cref="ReloadIfChanged"/> now and then (the config window does it every 2 seconds while it is open) to pick up hand edits.
/// A <see cref="FileChangeDetector"/> decides whether the file really changed: its time or length differ AND its content hash differs.
/// All methods are safe to call from several threads.
/// </summary>
public sealed class ExclusionList
{
    /// <summary>The name of the built-in copy of the default list inside the Iris.Core assembly.</summary>
    public const string DefaultResourceName = "Iris.Core.Detection.exclusions.default.txt";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly FileChangeDetector _detector;
    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    // True while a problem (locked or missing file) has already been logged, so we log it only once.
    private bool _problemLogged;

    // True when the list was replaced by a changed file (or an outside edit was found while appending) and ReloadIfChanged has not
    // reported it yet. Whoever happens to read the file first (a poll) only sets it; the next ReloadIfChanged reports it.
    private bool _changePending;

    private ExclusionList(string path, IClock clock, ILog log)
    {
        _path = path;
        _clock = clock;
        _log = log;
        _detector = new FileChangeDetector(path);
    }

    /// <summary>
    /// Opens the list at <paramref name="path"/>. If the file does not exist it is created (with its folder)
    /// from the built-in default list first. If the file exists but cannot be read right now, the list starts empty
    /// and <see cref="ReloadIfChanged"/> tries again later.
    /// </summary>
    public static ExclusionList LoadOrCreate(string path, IClock clock, ILog log)
    {
        var list = new ExclusionList(path, clock, log);
        if (!File.Exists(path))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, ReadDefaultText(), Utf8NoBom);
            log.Write(clock.UtcNow, "Exclusion list: created " + path + " from the built-in defaults.");
        }

        list.ReloadIfChanged();
        return list;
    }

    /// <summary>The text of the built-in default list (the file <c>exclusions.default.txt</c>).</summary>
    public static string ReadDefaultText()
    {
        using var stream = typeof(ExclusionList).Assembly.GetManifestResourceStream(DefaultResourceName)
                           ?? throw new InvalidOperationException(
                               "The built-in exclusion list is missing from the program (resource " + DefaultResourceName + ").");
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// True if the name is on the list. Ignores upper/lower case and spaces around the name; nothing else is tolerated: names match EXACTLY
    /// (the accuracy look-alike list of <see cref="LookAlikes"/> is empty), so a read <c>UNKN0WN</c> is not the excluded name <c>UNKNOWN</c>.
    /// </summary>
    public bool IsExcluded(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        lock (_gate)
            return _names.Contains(trimmed);
    }

    /// <summary>A copy of the names on the list, as written in the file (no particular order).</summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_gate)
                return _names.ToArray();
        }
    }

    /// <summary>How many names are on the list.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _names.Count;
        }
    }

    /// <summary>
    /// Re-reads the file if it changed from outside since IRIS last read or wrote it (see <see cref="FileChangeDetector"/>).
    /// If the file is locked or missing at the moment, the previous list is kept and the problem is logged once.
    /// Returns true when the list was reloaded from a changed file since the last call (even if the names turned out to be the same).
    /// </summary>
    public bool ReloadIfChanged()
    {
        lock (_gate)
        {
            ReloadCore();
            var changed = _changePending;
            _changePending = false;
            return changed;
        }
    }

    // Caller holds _gate. True when the list was replaced by the content of a changed file.
    private bool ReloadCore()
    {
        var change = _detector.Check();
        switch (change.Kind)
        {
            case FileChangeKind.Changed:
                SetNames(ParseNames(change.Text!));
                _problemLogged = false;
                _changePending = true;
                return true;

            case FileChangeKind.Missing:
                LogProblemOnce("the file is missing; keeping the list from before");
                return false;

            case FileChangeKind.Locked:
                LogProblemOnce("could not read the file (another program has it open); keeping the list from before");
                return false;

            default:
                _problemLogged = false;
                return false;
        }
    }

    // Caller holds _gate. Replaces the list.
    private void SetNames(HashSet<string> names)
    {
        _names = names;
    }

    /// <summary>The names in the text of an exclusion file: one per line, trimmed; blank lines and # comments are skipped.</summary>
    private static HashSet<string> ParseNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            names.Add(trimmed);
        }
        return names;
    }

    private void LogProblemOnce(string what)
    {
        if (_problemLogged)
            return;
        _problemLogged = true;
        _log.Write(_clock.UtcNow, "Exclusion list " + _path + ": " + what + ".");
    }
}
