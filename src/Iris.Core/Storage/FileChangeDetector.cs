using System.Text;
using Iris.Core.Config;

namespace Iris.Core.Storage;

/// <summary>What <see cref="FileChangeDetector.Check"/> found.</summary>
public enum FileChangeKind
{
    /// <summary>Nothing new: the file looks the same, or it was touched but its content is the same, or it is still missing.</summary>
    Unchanged,

    /// <summary>The content is different from the version IRIS knew. <see cref="FileChange.Text"/> and <see cref="FileChange.Hash"/> are set.</summary>
    Changed,

    /// <summary>The file does not exist (reported once; the following checks say <see cref="Unchanged"/> until the file is back).</summary>
    Missing,

    /// <summary>The file differs, but it could not be read right now (another program has it open). Nothing is remembered; the next check tries again.</summary>
    Locked,
}

/// <summary>The answer of <see cref="FileChangeDetector.Check"/>.</summary>
public sealed class FileChange
{
    private FileChange(FileChangeKind kind, string? text, string? hash)
    {
        Kind = kind;
        Text = text;
        Hash = hash;
    }

    public FileChangeKind Kind { get; }

    /// <summary>The new content as text (UTF-8, byte-order mark removed). Only set for <see cref="FileChangeKind.Changed"/>.</summary>
    public string? Text { get; }

    /// <summary>SHA-256 (lower-case hex) of the file's bytes. Only set for <see cref="FileChangeKind.Changed"/>.</summary>
    public string? Hash { get; }

    internal static readonly FileChange NoChange = new(FileChangeKind.Unchanged, null, null);
    internal static readonly FileChange Gone = new(FileChangeKind.Missing, null, null);
    internal static readonly FileChange InUse = new(FileChangeKind.Locked, null, null);

    internal static FileChange Modified(string text, string hash) => new(FileChangeKind.Changed, text, hash);
}

/// <summary>
/// Finds out whether a file was changed from outside (the user's editor, a sync tool), cheaply and without false alarms.
///
/// It remembers three things about the version of the file IRIS last loaded or wrote: the last-write time, the length and a SHA-256 of
/// the content. <see cref="Check"/> first only looks at the file's time and length (no reading). Only when either one is DIFFERENT
/// (not just newer: a restored older copy counts too) the file is read and hashed. If the hash is the one remembered, the file was merely
/// touched: the new time and length are remembered and the answer is Unchanged. Otherwise the answer is Changed, with the text.
/// A changed version is reported once: it becomes the remembered one, so a broken file is not reported again every check.
///
/// IRIS's own saves must call <see cref="RememberWritten"/> (or <see cref="RememberBytes"/> / <see cref="RememberFromDisk"/>) so they are never mistaken for outside edits.
/// Reading uses FileShare.ReadWrite, so an editor that keeps the file open does not block it; if the file is still locked the read is tried up to
/// 3 more times, 100 ms apart, and then given up quietly for this round (<see cref="FileChangeKind.Locked"/>).
/// Safe to call from several threads; the file is never read while holding the lock that <see cref="RememberWritten"/> needs.
/// </summary>
public sealed class FileChangeDetector
{
    /// <summary>How often a locked file is tried again within one check (after the first try).</summary>
    public const int RetryCount = 3;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<string, byte[]> _readBytes;

    // What IRIS knows about the file.
    private DateTime? _writeTime;
    private long _length;
    private string? _hash;
    private bool _missingReported;

    // Bumped by every remember call. A read that started before one of them is stale and its answer is dropped.
    private long _generation;

    /// <param name="path">The file to watch.</param>
    /// <param name="sleep">How to wait between read attempts. Null means <see cref="Thread.Sleep(TimeSpan)"/>. Tests pass a recorder.</param>
    /// <param name="readBytes">How to read the whole file. Null means a shared read (FileShare.ReadWrite | Delete). Tests pass a fake that throws.</param>
    public FileChangeDetector(string path, Action<TimeSpan>? sleep = null, Func<string, byte[]>? readBytes = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path must not be empty.", nameof(path));
        Path = path;
        _sleep = sleep ?? (span => Thread.Sleep(span));
        _readBytes = readBytes ?? (file => ReadSharedBytes(file));
    }

    public string Path { get; }

    /// <summary>The SHA-256 of the version IRIS last loaded, wrote or reported, or null if there is none.</summary>
    public string? KnownHash
    {
        get
        {
            lock (_gate)
                return _hash;
        }
    }

    /// <summary>SHA-256 of the UTF-8 (no byte-order mark) bytes of <paramref name="text"/>, the way IRIS writes its files.</summary>
    public static string HashOf(string text) => HostedConfigClient.Sha256Hex(Utf8NoBom.GetBytes(text));

    /// <summary>
    /// Compares the file with the remembered version. Stats the file; reads it only when its time or length differ.
    /// Never throws for a file problem.
    /// </summary>
    public FileChange Check()
    {
        DateTime time;
        long length;
        long generation;
        lock (_gate)
        {
            if (!TryStat(out var exists, out time, out length))
                return FileChange.InUse;

            if (!exists)
            {
                if (_missingReported)
                    return FileChange.NoChange;
                _missingReported = true;
                return FileChange.Gone;
            }

            _missingReported = false;
            if (_writeTime == time && _length == length)
                return FileChange.NoChange;

            generation = _generation;
        }

        return ReadAndRemember(time, length, generation, forced: false);
    }

    /// <summary>
    /// Reads the file now, whatever was remembered, and remembers what it finds (used when IRIS loads the file at start).
    /// Changed = the file's content (always, even if it equals the remembered one); Missing = no such file (remembered as missing).
    /// </summary>
    public FileChange Load()
    {
        DateTime time;
        long length;
        long generation;
        lock (_gate)
        {
            if (!TryStat(out var exists, out time, out length))
                return FileChange.InUse;

            if (!exists)
            {
                _writeTime = null;
                _length = 0;
                _hash = null;
                _missingReported = true;
                _generation++;
                return FileChange.Gone;
            }

            _missingReported = false;
            generation = _generation;
        }

        return ReadAndRemember(time, length, generation, forced: true);
    }

    /// <summary>
    /// IRIS just wrote <paramref name="text"/> to the file (UTF-8, no byte-order mark, as <see cref="AtomicFile"/> does):
    /// remember that version so the next check does not see it as an outside edit.
    /// </summary>
    public void RememberWritten(string text) => Remember(HashOf(text));

    /// <summary>
    /// IRIS just changed the file and knows its complete content as bytes (for example the exclusion list after an append, read back through
    /// the stream that wrote it): remember that version. Unlike <see cref="RememberFromDisk"/> the file is not opened again, so
    /// the remembered hash is exactly the content that was written and read back (only the time and length are looked up now).
    /// </summary>
    public void RememberBytes(byte[] bytes) => Remember(HostedConfigClient.Sha256Hex(bytes));

    private void Remember(string hash)
    {
        lock (_gate)
        {
            _generation++;
            _hash = hash;
            _missingReported = false;
            if (TryStat(out var exists, out var time, out var length) && exists)
            {
                _writeTime = time;
                _length = length;
            }
            else
            {
                _writeTime = null; // cannot tell; the next check reads the file once and finds the same hash
            }
        }
    }

    /// <summary>
    /// IRIS just changed the file in a way it cannot easily describe as text (the exclusion list is appended to):
    /// read it as it is now and remember that. If it cannot be read right now the next check will look again.
    /// </summary>
    public void RememberFromDisk()
    {
        long generation;
        lock (_gate)
            generation = ++_generation;

        var bytes = ReadWithRetries();
        lock (_gate)
        {
            if (generation != _generation)
                return;
            if (bytes is not null && TryStat(out var exists, out var time, out var length) && exists)
            {
                _hash = HostedConfigClient.Sha256Hex(bytes);
                _writeTime = time;
                _length = length;
                _missingReported = false;
            }
            else
            {
                _writeTime = null;
            }
        }
    }

    // ---------------------------------------------------------------------------------------

    private FileChange ReadAndRemember(DateTime time, long length, long generation, bool forced)
    {
        var bytes = ReadWithRetries();
        if (bytes is null)
            return FileChange.InUse;

        var hash = HostedConfigClient.Sha256Hex(bytes);
        lock (_gate)
        {
            if (generation != _generation)
                return FileChange.NoChange; // IRIS saved the file while we were reading: that save knows better

            _writeTime = time;
            _length = length;
            if (!forced && _hash == hash)
                return FileChange.NoChange; // only touched, same content

            _hash = hash;
            return FileChange.Modified(ConfigImporter.DecodeUtf8(bytes), hash);
        }
    }

    // Caller holds _gate. False when the file's state could not even be read.
    private bool TryStat(out bool exists, out DateTime time, out long length)
    {
        exists = false;
        time = default;
        length = 0;
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
                return true;
            exists = true;
            time = info.LastWriteTimeUtc;
            length = info.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The file's bytes, or null if it is vanished or still locked after the retries.</summary>
    private byte[]? ReadWithRetries()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _readBytes(Path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null; // gone between the stat and the read: the next check reports it as missing
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= RetryCount)
                    return null;
                _sleep(RetryDelay);
            }
        }
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
