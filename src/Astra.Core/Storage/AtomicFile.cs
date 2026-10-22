using System.Text;

namespace Astra.Core.Storage;

/// <summary>
/// Crash-safe text file writing, shared by the list file, the settings file and exports.
/// The new text goes to "name.tmp" first (flushed to the disk) and then replaces the real file in one step,
/// so a crash or power cut never leaves a half-written file.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="text"/> (UTF-8, no BOM) to <paramref name="path"/>. The folder is created if needed.
    /// With <paramref name="keepBackup"/> the previous version stays as "name.bak" (one backup, overwritten on every write).
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file cannot be written;
    /// in that case the temp file is removed and, if the swap went wrong half-way, the backup is put back.
    /// </summary>
    public static void WriteText(string path, string text, bool keepBackup)
    {
        var temp = path + ".tmp";
        var backup = path + ".bak";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write and flush to the disk before the swap, so a power cut cannot leave an empty "new" file.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // File.Replace needs an existing target; the very first write has none, so it is a plain move.
            if (File.Exists(path))
                File.Replace(temp, path, keepBackup ? backup : null);
            else
                File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed swap can leave the real file missing while the backup exists: put the backup back.
            if (keepBackup)
                TryRestoreFromBackup(path, backup);
            TryDelete(temp);
            throw;
        }
    }

    private static void TryRestoreFromBackup(string path, string backup)
    {
        try
        {
            if (!File.Exists(path) && File.Exists(backup))
                File.Copy(backup, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
