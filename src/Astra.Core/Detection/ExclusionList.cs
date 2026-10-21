using System.Globalization;
using System.Reflection;
using System.Text;

namespace Astra.Core.Detection;

/// <summary>
/// The exclusion list: names that show up on screen but are not players (stars, planets, stations, ship names...).
/// A nameplate with one of these names is never matched or looked up (see <see cref="NameFilter"/>).
///
/// The list lives in a plain text file, <c>exclusions.txt</c>, that the user can edit by hand:
/// one name per line, matched without regard to upper or lower case; blank lines are ignored;
/// a line starting with # is a comment. The first time ASTRA runs, the file is created from the list that ships inside
/// the program (<c>exclusions.default.txt</c>). Where the file lives is decided by the app layer.
///
/// ASTRA itself only ever APPENDS to the file (<see cref="AddAutomatic"/>); it never rewrites what the user wrote.
/// Call <see cref="ReloadIfChanged"/> now and then (for example once a second) to pick up hand edits.
/// All methods are safe to call from several threads.
/// </summary>
public sealed class ExclusionList
{
    /// <summary>The name of the built-in copy of the default list inside the Astra.Core assembly.</summary>
    public const string DefaultResourceName = "Astra.Core.Detection.exclusions.default.txt";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly object _gate = new();
    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    // What the file looked like when we last read it successfully. Null = never read.
    private DateTime? _lastWriteUtc;
    private long _length;

    // True while a problem (locked or missing file) has already been logged, so we log it only once.
    private bool _problemLogged;

    private ExclusionList(string path, IClock clock, ILog log)
    {
        _path = path;
        _clock = clock;
        _log = log;
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

    /// <summary>True if the name is on the list. Ignores upper/lower case and spaces around the name.</summary>
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
    /// Re-reads the file if its last-write time or length changed since the last read.
    /// If the file is locked or missing at the moment, the previous list is kept and the problem is logged once.
    /// </summary>
    public void ReloadIfChanged()
    {
        lock (_gate)
            ReloadCore();
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
            // The user may have added this name by hand since the last read.
            ReloadCore();
            if (_names.Contains(trimmed))
                return false;

            var date = _clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var cleanReason = (reason ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            var text = "# auto-added " + date + " UTC: " + cleanReason + "\n" + trimmed + "\n";

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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(_clock.UtcNow, "Exclusion list: could not add \"" + trimmed + "\" to " + _path + ": " + ex.Message);
                return false;
            }

            _names.Add(trimmed);
            _log.Write(_clock.UtcNow, "Exclusion list: added \"" + trimmed + "\" automatically (" + cleanReason + ").");
            return true;
        }
    }

    // Caller holds _gate.
    private void ReloadCore()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                LogProblemOnce("the file is missing; keeping the list from before");
                return;
            }

            var writeTime = info.LastWriteTimeUtc;
            var length = info.Length;
            if (_lastWriteUtc == writeTime && _length == length)
                return;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                        continue;
                    names.Add(trimmed);
                }
            }

            _names = names;
            _lastWriteUtc = writeTime;
            _length = length;
            _problemLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogProblemOnce("could not read the file (" + ex.Message + "); keeping the list from before");
        }
    }

    private void LogProblemOnce(string what)
    {
        if (_problemLogged)
            return;
        _problemLogged = true;
        _log.Write(_clock.UtcNow, "Exclusion list " + _path + ": " + what + ".");
    }
}
