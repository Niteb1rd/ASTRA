using System.Text;
using Iris.Core.Config;

namespace Iris.Core.Storage;

/// <summary>The last-known-good copy of a config file: its text, when it was written, and the SHA-256 of that text.</summary>
public sealed record LastGoodCopy(string Text, DateTimeOffset WrittenUtc, string Hash);

/// <summary>
/// One config file IRIS keeps an eye on (the list file or the settings file): its path, the <see cref="FileChangeDetector"/> that
/// notices outside edits, and its last-known-good copy (<c>name.lastgood.toml</c> next to it).
///
/// Rules:
/// - Every time the file is loaded or saved and is valid (no errors; warnings are fine) its exact text is also written to the last-good file
///   (atomically, no backup of the backup). The main file is never touched because of the last-good copy.
/// - IRIS's own saves go through <see cref="Wrote"/>, which tells the detector, so they are never reported as outside edits.
/// - <see cref="AppliedHash"/> is the hash of the version the app is currently using; a file that changes back to exactly that version
///   is "restored", not "new".
/// </summary>
public sealed class WatchedFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IClock _clock;
    private readonly ILog _log;

    // One thread at a time writes the last-good file (a poll on a background thread and a save on the UI thread can meet here).
    private readonly object _lastGoodGate = new();

    // Guards the two hashes below, which are read on a background thread (the poll) and written on the UI thread (saves, applies).
    private readonly object _hashGate = new();
    private string? _appliedHash;
    private string? _ownWriteHash;

    public WatchedFile(string path, string lastGoodPath, IClock clock, ILog? log = null, FileChangeDetector? detector = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path must not be empty.", nameof(path));
        if (string.IsNullOrWhiteSpace(lastGoodPath))
            throw new ArgumentException("The last-good path must not be empty.", nameof(lastGoodPath));
        Path = path;
        LastGoodPath = lastGoodPath;
        _clock = clock;
        _log = log ?? NullLog.Instance;
        Detector = detector ?? new FileChangeDetector(path);
    }

    /// <summary>The main file (for example Data\social-overlay.toml).</summary>
    public string Path { get; }

    /// <summary>The last-known-good copy (for example Data\social-overlay.lastgood.toml).</summary>
    public string LastGoodPath { get; }

    public FileChangeDetector Detector { get; }

    /// <summary>Hash of the version of the file the app is using now (loaded, imported, saved or applied from an outside edit). Null if none.</summary>
    public string? AppliedHash
    {
        get
        {
            lock (_hashGate)
                return _appliedHash;
        }
        set
        {
            lock (_hashGate)
                _appliedHash = value;
        }
    }

    /// <summary>Hash of what IRIS itself last wrote to the file, or null.</summary>
    public string? OwnWriteHash
    {
        get
        {
            lock (_hashGate)
                return _ownWriteHash;
        }
    }

    /// <summary>
    /// IRIS just saved <paramref name="text"/> (valid by construction) to the main file: remember it so it is not seen as an outside edit,
    /// treat it as the version in use, and keep it as the last-good copy.
    /// </summary>
    public void Wrote(string text)
    {
        Detector.RememberWritten(text);
        var hash = FileChangeDetector.HashOf(text);
        lock (_hashGate)
        {
            _ownWriteHash = hash;
            _appliedHash = hash;
        }
        WriteLastGood(text);
    }

    /// <summary>
    /// Stores <paramref name="text"/> (the exact text of a valid main file) as the last-good copy. Does nothing if the copy already has exactly
    /// that text. A copy that cannot be written is logged and otherwise ignored: it is a safety net, not part of saving.
    /// </summary>
    public void WriteLastGood(string text)
    {
        lock (_lastGoodGate)
        {
            try
            {
                if (File.Exists(LastGoodPath) && string.Equals(File.ReadAllText(LastGoodPath, Utf8NoBom), text, StringComparison.Ordinal))
                    return;
                AtomicFile.WriteText(LastGoodPath, text, keepBackup: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(_clock.UtcNow, "Could not write " + System.IO.Path.GetFileName(LastGoodPath) + ": " + ex.Message);
            }
        }
    }

    /// <summary>The last-good copy, or null if there is none or it cannot be read.</summary>
    public LastGoodCopy? ReadLastGood()
    {
        try
        {
            if (!File.Exists(LastGoodPath))
                return null;
            var bytes = File.ReadAllBytes(LastGoodPath);
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(LastGoodPath), TimeSpan.Zero);
            return new LastGoodCopy(ConfigImporter.DecodeUtf8(bytes), written, HostedConfigClient.Sha256Hex(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(_clock.UtcNow, "Could not read " + System.IO.Path.GetFileName(LastGoodPath) + ": " + ex.Message);
            return null;
        }
    }

    internal void Log(string message) => _log.Write(_clock.UtcNow, message);
}
