using System.Globalization;
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
/// IRIS itself only ever APPENDS to the file (<see cref="AddAutomatic"/>); it never rewrites what the user wrote.
/// Call <see cref="ReloadIfChanged"/> now and then (the config window does it every 2 seconds while it is open) to pick up hand edits.
/// A <see cref="FileChangeDetector"/> decides whether the file really changed: its time or length differ AND its content hash differs,
/// and IRIS's own appends are never mistaken for outside edits.
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

    // The same names in the accuracy look-alike form (0/O and 1/I folded together, see LookAlikes): what IsExcluded compares with.
    private HashSet<string> _folded = new(StringComparer.Ordinal);

    // True while a problem (locked or missing file) has already been logged, so we log it only once.
    private bool _problemLogged;

    // True when the list was replaced by a changed file (or an outside edit was found while appending) and ReloadIfChanged has not
    // reported it yet. Whoever happens to read the file first (a poll, or AddAutomatic) only sets it; the next ReloadIfChanged reports it.
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
    /// True if the name is on the list. Ignores upper/lower case and spaces around the name, and tolerates the accuracy look-alikes of
    /// <see cref="LookAlikes"/> (<c>0</c> for <c>O</c> and <c>1</c> for <c>I</c>, either way): a nameplate read as <c>UNKN0WN</c> is the
    /// excluded name <c>UNKNOWN</c>. No other difference is tolerated.
    /// </summary>
    public bool IsExcluded(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        lock (_gate)
            return _names.Contains(trimmed) || _folded.Contains(LookAlikes.FoldName(trimmed));
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
    /// Returns true when the list was reloaded from a changed file since the last call (even if the names turned out to be the same):
    /// a change that <see cref="AddAutomatic"/> noticed on its own is reported here too, once.
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

    /// <summary>
    /// Adds a name the program found out on its own. Appends two lines to the END of the file:
    /// <c># auto-added yyyy-MM-dd UTC: reason</c> and then the name. Nothing the user wrote is changed.
    /// Returns false, and writes nothing, if the name is already on the list (or is empty / has a line break in it).
    /// Also returns false if the file could not be written; that is logged.
    /// </summary>
    public bool AddAutomatic(string name, string reason)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
            return false;

        lock (_gate)
        {
            // The user may have added this name by hand since the last read. (If the file changed, ReloadCore leaves the "changed"
            // flag set for the next ReloadIfChanged; this call must not swallow that notification.)
            ReloadCore();
            if (_names.Contains(trimmed) || _folded.Contains(LookAlikes.FoldName(trimmed)))
                return false;

            var date = _clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var cleanReason = (reason ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            var text = "# auto-added " + date + " UTC: " + cleanReason + "\n" + trimmed + "\n";

            // What the file holds after our append, read back through the stream that wrote it (null if that read failed).
            byte[]? written = null;
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                        text = "\n" + text;
                }

                stream.Seek(0, SeekOrigin.End);
                var bytes = Utf8NoBom.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                try
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    using var all = new MemoryStream();
                    stream.CopyTo(all);
                    written = all.ToArray();
                }
                catch (IOException)
                {
                    // the append worked; only the read-back did not (handled below)
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(_clock.UtcNow, "Exclusion list: could not add \"" + trimmed + "\" to " + _path + ": " + ex.Message);
                return false;
            }

            if (written is null)
            {
                _detector.RememberFromDisk(); // our own append is not an outside edit
                _names.Add(trimmed);
                _folded.Add(LookAlikes.FoldName(trimmed));
            }
            else
            {
                // Remember exactly what we wrote and read back, and take the names from the same bytes. If the file also holds something
                // the previous read did not know (an outside edit that came in between), that is a change to report at the next poll.
                _detector.RememberBytes(written);
                var onDisk = ParseNames(ConfigImporter.DecodeUtf8(written));
                var expected = new HashSet<string>(_names, StringComparer.OrdinalIgnoreCase) { trimmed };
                if (!onDisk.SetEquals(expected))
                    _changePending = true;
                SetNames(onDisk);
            }

            _log.Write(_clock.UtcNow, "Exclusion list: added \"" + trimmed + "\" automatically (" + cleanReason + ").");
            return true;
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

    // Caller holds _gate. Replaces the list (and its look-alike form).
    private void SetNames(HashSet<string> names)
    {
        _names = names;
        _folded = new HashSet<string>(names.Select(LookAlikes.FoldName), StringComparer.Ordinal);
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
