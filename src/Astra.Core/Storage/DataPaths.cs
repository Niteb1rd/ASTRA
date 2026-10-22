namespace Astra.Core.Storage;

/// <summary>
/// Where ASTRA keeps its files. Everything lives in one "Data" folder next to the program (installed or portable),
/// so deleting that folder resets ASTRA and copying it moves ASTRA's state to another PC.
/// </summary>
public sealed class DataPaths
{
    public const string DataFolderName = "Data";
    public const string SocialListFileName = "social-overlay.toml";
    public const string SettingsFileName = "settings.toml";
    public const string CacheFileName = "cache.db";
    public const string ExclusionsFileName = "exclusions.txt";
    public const string LogsFolderName = "logs";

    private DataPaths(string dataFolder)
    {
        DataFolder = dataFolder;
    }

    /// <summary>Builds the paths for a program that lives in <paramref name="exeDirectory"/>. Creates nothing yet.</summary>
    public static DataPaths FromBaseDirectory(string exeDirectory)
    {
        if (string.IsNullOrWhiteSpace(exeDirectory))
            throw new ArgumentException("The program folder must not be empty.", nameof(exeDirectory));
        return new DataPaths(Path.Combine(exeDirectory, DataFolderName));
    }

    /// <summary>The "Data" folder.</summary>
    public string DataFolder { get; }

    /// <summary>The player, org and org-rule list (TOML, shareable).</summary>
    public string SocialListFile => Path.Combine(DataFolder, SocialListFileName);

    /// <summary>This PC's app settings (TOML). Never shared or exported with the lists.</summary>
    public string SettingsFile => Path.Combine(DataFolder, SettingsFileName);

    /// <summary>The RSI lookup cache (SQLite).</summary>
    public string CacheFile => Path.Combine(DataFolder, CacheFileName);

    /// <summary>The exclusion list (plain text).</summary>
    public string ExclusionsFile => Path.Combine(DataFolder, ExclusionsFileName);

    /// <summary>Session log files.</summary>
    public string LogsFolder => Path.Combine(DataFolder, LogsFolderName);

    /// <summary>Creates the Data folder and the logs folder if they are missing. Safe to call every start.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogsFolder);
    }
}
