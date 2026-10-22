using System.Globalization;
using System.Text;
using Astra.Core.Storage;

namespace Astra.Core.Config;

/// <summary>What to do with the list file that already exists when a config is imported. The user picks one every time.</summary>
public enum ImportMode
{
    /// <summary>Rename the current file to "name.backup-yyyyMMdd-HHmmss.toml", then write the imported config as the list file.</summary>
    BackupExisting,

    /// <summary>Keep the current rows and add the imported ones that are new (the current rows win on a clash).</summary>
    Merge,

    /// <summary>The imported config overwrites the list file.</summary>
    Replace,
}

/// <summary>What <see cref="ConfigImporter.Import"/> did.</summary>
public sealed class ImportResult
{
    private ImportResult()
    {
    }

    public bool Success { get; private init; }

    /// <summary>Plain-words reason. Only set when the import failed (nothing was changed in that case).</summary>
    public string? Error { get; private init; }

    public ImportMode Mode { get; private init; }

    /// <summary>False when there was no list file yet, so the import simply created it (the mode made no difference).</summary>
    public bool HadExistingFile { get; private init; }

    /// <summary>The list as it is now in the file. Null when the import failed.</summary>
    public SocialConfig? Config { get; private init; }

    /// <summary>Rows that came from the imported file (all of its rows, unless merged: then only the new ones).</summary>
    public int PlayersAdded { get; private init; }
    public int OrgsAdded { get; private init; }
    public int RulesAdded { get; private init; }

    /// <summary>Merge only: imported rows left out because the list already had them.</summary>
    public int DuplicatesSkipped { get; private init; }

    /// <summary>BackupExisting only: where the old file went.</summary>
    public string? BackupPath { get; private init; }

    /// <summary>One line for the status bar, e.g. "Imported: 3 players, 1 org, 2 rules added".</summary>
    public string Summary
    {
        get
        {
            if (!Success)
                return Error ?? "Import failed.";

            var counts = $"{Count(PlayersAdded, "player")}, {Count(OrgsAdded, "org")}, {Count(RulesAdded, "rule")}";
            if (!HadExistingFile || Mode == ImportMode.BackupExisting)
                return "Imported: " + counts
                    + (BackupPath is null ? string.Empty : " (old file kept as " + Path.GetFileName(BackupPath) + ")");
            if (Mode == ImportMode.Replace)
                return "Imported: " + counts + " (your lists were replaced)";
            return "Imported: " + counts + " added" + (DuplicatesSkipped > 0 ? $", {Count(DuplicatesSkipped, "duplicate")} skipped" : string.Empty);
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    internal static ImportResult Fail(ImportMode mode, string error) => new() { Success = false, Error = error, Mode = mode };

    internal static ImportResult Done(
        ImportMode mode, bool hadExisting, SocialConfig config, int players, int orgs, int rules, int skipped, string? backupPath) =>
        new()
        {
            Success = true,
            Mode = mode,
            HadExistingFile = hadExisting,
            Config = config,
            PlayersAdded = players,
            OrgsAdded = orgs,
            RulesAdded = rules,
            DuplicatesSkipped = skipped,
            BackupPath = backupPath,
        };
}

/// <summary>Text read from a file, or the reason it could not be read.</summary>
public sealed class ImportTextResult
{
    private ImportTextResult(string? text, string? error)
    {
        Text = text;
        Error = error;
    }

    public bool Success => Error is null;
    public string? Text { get; }
    public string? Error { get; }

    internal static ImportTextResult Ok(string text) => new(text, null);
    internal static ImportTextResult Failed(string error) => new(null, error);
}

/// <summary>
/// The file side of importing and exporting a shareable config. The window decides WHICH mode; this class does it,
/// and does it safely: when anything goes wrong the user's list file is left as it was.
/// Imported text is only ever parsed as data (see <see cref="SocialConfigToml.Read"/>).
/// </summary>
public static class ConfigImporter
{
    /// <summary>Largest config file or hosted file ASTRA will read (1 MB; a real list is a few KB).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private const string BackupTimeFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Applies <paramref name="imported"/> to the list file at <paramref name="listFilePath"/> using <paramref name="mode"/>.
    /// With no list file yet the imported config is simply written (every mode then means the same thing).
    /// </summary>
    public static ImportResult Import(string listFilePath, SocialConfig imported, ImportMode mode, IClock clock)
    {
        if (!File.Exists(listFilePath))
        {
            var created = SocialListStore.Save(listFilePath, imported);
            return created.Success
                ? ImportResult.Done(mode, hadExisting: false, imported, imported.Players.Count, imported.Orgs.Count, imported.OrgRules.Count, 0, null)
                : ImportResult.Fail(mode, created.Error ?? "The imported config could not be saved.");
        }

        switch (mode)
        {
            case ImportMode.Replace:
            {
                var saved = SocialListStore.Save(listFilePath, imported);
                return saved.Success
                    ? ImportResult.Done(mode, true, imported, imported.Players.Count, imported.Orgs.Count, imported.OrgRules.Count, 0, null)
                    : ImportResult.Fail(mode, saved.Error ?? "The imported config could not be saved.");
            }

            case ImportMode.Merge:
            {
                var current = SocialListStore.Load(listFilePath);
                if (!current.IsValid)
                    return ImportResult.Fail(mode,
                        "Your current list file has problems, so it cannot be merged safely. Choose \"Backup existing config\" or \"Replace existing config\" instead.");

                var merged = SocialConfigMerger.Merge(current.Config, imported);
                var saved = SocialListStore.Save(listFilePath, merged.Config);
                return saved.Success
                    ? ImportResult.Done(mode, true, merged.Config, merged.AddedPlayers, merged.AddedOrgs, merged.AddedRules, merged.SkippedDuplicates, null)
                    : ImportResult.Fail(mode, saved.Error ?? "The merged config could not be saved.");
            }

            case ImportMode.BackupExisting:
                return ImportWithBackup(listFilePath, imported, clock);

            default:
                return ImportResult.Fail(mode, "Unknown import mode.");
        }
    }

    private static ImportResult ImportWithBackup(string listFilePath, SocialConfig imported, IClock clock)
    {
        const ImportMode mode = ImportMode.BackupExisting;
        string backupPath;
        try
        {
            backupPath = UniqueBackupPath(listFilePath, clock.UtcNow);
            File.Move(listFilePath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImportResult.Fail(mode, "The current list file could not be backed up, so nothing was changed: " + ex.Message);
        }

        var saved = SocialListStore.Save(listFilePath, imported);
        if (!saved.Success)
        {
            var error = saved.Error ?? "The imported config could not be saved.";
            var safeNote = " Your previous list is safe as " + Path.GetFileName(backupPath) + ".";

            // The old file was moved away first: put it back where it was, unless something is at that path now.
            if (File.Exists(listFilePath))
                return ImportResult.Fail(mode, error + safeNote);
            try
            {
                File.Move(backupPath, listFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ImportResult.Fail(mode, error + safeNote);
            }
            return ImportResult.Fail(mode, error);
        }

        return ImportResult.Done(mode, true, imported, imported.Players.Count, imported.Orgs.Count, imported.OrgRules.Count, 0, backupPath);
    }

    /// <summary>"social-overlay.backup-20261021-234015.toml" in the same folder; "-2", "-3"... is added if that name is taken.</summary>
    private static string UniqueBackupPath(string listFilePath, DateTimeOffset utcNow)
    {
        var fullPath = Path.GetFullPath(listFilePath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var stamp = utcNow.ToLocalTime().ToString(BackupTimeFormat, CultureInfo.InvariantCulture);

        var candidate = Path.Combine(directory, $"{baseName}.backup-{stamp}{extension}");
        for (int n = 2; File.Exists(candidate) && n < 1000; n++)
            candidate = Path.Combine(directory, $"{baseName}.backup-{stamp}-{n}{extension}");
        return candidate;
    }

    /// <summary>Reads a config file the user picked. Refuses files over <see cref="MaxFileBytes"/>.</summary>
    public static ImportTextResult ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return ImportTextResult.Failed("The file does not exist.");
            if (info.Length > MaxFileBytes)
                return ImportTextResult.Failed("The file is too large (over 1 MB). A config file is normally a few KB.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxFileBytes)
                    return ImportTextResult.Failed("The file is too large (over 1 MB). A config file is normally a few KB.");
            }
            return ImportTextResult.Ok(DecodeUtf8(buffer.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ImportTextResult.Failed("The file could not be read: " + ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="config"/> as a shareable config file to a path the user picked (crash-safe, no .bak).
    /// Never throws for a file problem (read-only USB stick, missing folder, bad path...): the reason comes back in the result.
    /// </summary>
    public static SaveResult Export(string path, SocialConfig config)
    {
        try
        {
            var text = SocialConfigToml.Write(config);
            var check = SocialConfigToml.Read(text);
            if (!check.IsValid)
                return SaveResult.Failed("The list is not valid and was not exported: " + string.Join(" ", check.Errors));

            AtomicFile.WriteText(path, text, keepBackup: false);
            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return SaveResult.Failed("The file could not be written: " + ex.Message);
        }
    }

    /// <summary>
    /// Parses imported text for an import: <see cref="SocialConfigToml.Read"/> plus the rule-value check
    /// (an archetype or activity rule must use a value ASTRA knows, see <see cref="OrgRuleValues"/>). Bad values are ERRORS here,
    /// so nothing is imported. (Loading the user's own list file does not apply this check, so an old file never becomes unreadable.)
    /// </summary>
    public static ConfigLoadResult ParseForImport(string text)
    {
        var read = SocialConfigToml.Read(text);
        if (read.Config is not null)
            read.Errors.AddRange(OrgRuleValues.Check(read.Config));
        return read;
    }

    /// <summary>UTF-8 to text, dropping a leading byte-order mark (some editors add one).</summary>
    internal static string DecodeUtf8(byte[] bytes)
    {
        var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }
}
