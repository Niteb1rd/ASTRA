using System.Globalization;
using System.Text;

namespace Astra.Core.Logging;

/// <summary>
/// Writes log lines to one file per day: <c>astra-yyyy-MM-dd.log</c> in the logs folder (the day is the local date of the
/// line's timestamp). One line per entry: <c>yyyy-MM-dd HH:mm:ss.fff  message</c> in local time. UTF-8 without BOM.
/// The file is opened shared, so Notepad can show it while ASTRA keeps writing. <see cref="Write"/> never throws:
/// a log that crashes the app is worse than a missing line.
/// </summary>
public sealed class FileLog : ILog
{
    public const string FilePrefix = "astra-";
    public const string FileExtension = ".log";
    private const string DateFormat = "yyyy-MM-dd";
    private const string LineTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private string? _lastError;
    private volatile bool _enabled = true;

    public FileLog(string folder, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("The log folder must not be empty.", nameof(folder));
        Folder = folder;
        _clock = clock;
    }

    /// <summary>
    /// When false, <see cref="Write"/> does nothing (the "write a log file" setting). Can be switched at any time, from any thread.
    /// Existing files are left alone; <see cref="DeleteOlderThan"/> works regardless.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>The folder the daily files live in.</summary>
    public string Folder { get; }

    /// <summary>Today's file (today according to the clock). It may not exist yet.</summary>
    public string CurrentFilePath => PathForDay(_clock.UtcNow);

    /// <summary>The last write problem (for example a locked or missing folder), or null. Only for diagnostics.</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
                return _lastError;
        }
    }

    public void Write(DateTimeOffset utcTime, string message)
    {
        if (!_enabled)
            return;

        try
        {
            var local = utcTime.ToLocalTime();
            var line = local.ToString(LineTimeFormat, CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine;
            var bytes = Utf8NoBom.GetBytes(line);
            var path = PathForDay(utcTime);

            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
            }
        }
        catch
        {
            // never throw from a log
        }
    }

    /// <summary>
    /// Deletes <c>astra-yyyy-MM-dd.log</c> files in <paramref name="folder"/> whose date (read from the file NAME, not the
    /// file's timestamps) is more than <paramref name="age"/> before today. Other files and names that do not parse are left alone,
    /// and so is any file that cannot be deleted. Returns how many files were deleted.
    /// </summary>
    public static int DeleteOlderThan(string folder, TimeSpan age, DateTimeOffset now)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(folder))
                return 0;

            var cutoff = now.ToLocalTime().Date - age;
            foreach (var file in Directory.EnumerateFiles(folder, FilePrefix + "*" + FileExtension))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var datePart = name.Substring(FilePrefix.Length);
                    if (!DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        continue;
                    if (day.Date >= cutoff)
                        continue;
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // in use, read-only, gone already: skip it
                }
            }
        }
        catch
        {
            // folder unreadable: nothing to clean
        }
        return deleted;
    }

    private string PathForDay(DateTimeOffset utcTime)
    {
        var day = utcTime.ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
        return Path.Combine(Folder, FilePrefix + day + FileExtension);
    }
}
