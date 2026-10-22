using Iris.Core.Config;
using Iris.Core.Settings;

namespace Iris.Core.Storage;

/// <summary>How the list file was loaded at startup (see <see cref="ConfigStartup.LoadSocialList"/>).</summary>
public sealed class SocialListStartup
{
    public SocialListStartup(
        SocialListLoadResult load, bool usedLastGood, DateTimeOffset? lastGoodTime, IReadOnlyList<string> mainFileErrors, string? mainFileHash)
    {
        Load = load;
        UsedLastGood = usedLastGood;
        LastGoodTime = lastGoodTime;
        MainFileErrors = mainFileErrors;
        MainFileHash = mainFileHash;
    }

    /// <summary>The list the app works with: the main file, or its last-good copy when the main file has errors.</summary>
    public SocialListLoadResult Load { get; }

    /// <summary>True when the main file has errors and the last-good copy is used instead. The main file was NOT changed.</summary>
    public bool UsedLastGood { get; }

    /// <summary>When the last-good copy was written. Only set when <see cref="UsedLastGood"/>.</summary>
    public DateTimeOffset? LastGoodTime { get; }

    /// <summary>What is wrong with the main file. Empty when the main file is fine (or missing).</summary>
    public IReadOnlyList<string> MainFileErrors { get; }

    /// <summary>Hash of the main file when it has errors and could be read, otherwise null.</summary>
    public string? MainFileHash { get; }

    public bool MainFileHasErrors => MainFileErrors.Count > 0;
}

/// <summary>How the settings file was loaded at startup (see <see cref="ConfigStartup.LoadSettings"/>).</summary>
public sealed class SettingsStartup
{
    public SettingsStartup(
        AppSettingsLoadResult load, bool usedLastGood, DateTimeOffset? lastGoodTime, IReadOnlyList<string> mainFileErrors, string? mainFileHash)
    {
        Load = load;
        UsedLastGood = usedLastGood;
        LastGoodTime = lastGoodTime;
        MainFileErrors = mainFileErrors;
        MainFileHash = mainFileHash;
    }

    /// <summary>The settings the app works with: the main file, its last-good copy, or the defaults.</summary>
    public AppSettingsLoadResult Load { get; }

    public bool UsedLastGood { get; }

    public DateTimeOffset? LastGoodTime { get; }

    /// <summary>What is wrong with the main file (parse error, newer format, unreadable). Empty when it is fine or missing.</summary>
    public IReadOnlyList<string> MainFileErrors { get; }

    public string? MainFileHash { get; }

    public bool MainFileHasErrors => MainFileErrors.Count > 0;
}

/// <summary>
/// What IRIS does with its two config files when it starts. Lives in Core so the decision can be tested.
///
/// - A valid file is used, and its exact text is stored as the last-good copy.
/// - A missing file means a fresh start (empty list / default settings).
/// - A file with errors: if a valid last-good copy exists it is used instead (the main file is left exactly as it is, so the user can fix it);
///   without one, today's behaviour: the list file's valid rows are loaded and the problems are reported; the settings are the defaults.
/// </summary>
public static class ConfigStartup
{
    public const string FileInUseMessage = "The file could not be read (another program may have it open).";

    public static SocialListStartup LoadSocialList(WatchedFile file)
    {
        var read = file.Detector.Load();
        if (read.Kind == FileChangeKind.Missing)
        {
            file.AppliedHash = null;
            return new SocialListStartup(
                new SocialListLoadResult(new ConfigLoadResult { Config = new SocialConfig() }, isNew: true),
                usedLastGood: false, lastGoodTime: null, Array.Empty<string>(), mainFileHash: null);
        }

        ConfigLoadResult parsed;
        if (read.Kind == FileChangeKind.Changed)
        {
            parsed = SocialConfigToml.Read(read.Text!);
            if (parsed.IsValid)
            {
                file.WriteLastGood(read.Text!);
                file.AppliedHash = read.Hash;
                return new SocialListStartup(new SocialListLoadResult(parsed, isNew: false), false, null, Array.Empty<string>(), null);
            }
        }
        else
        {
            parsed = new ConfigLoadResult();
            parsed.Errors.Add(FileInUseMessage);
        }

        var errors = parsed.Errors.ToList();
        var hash = read.Hash;
        file.Log($"{Path.GetFileName(file.Path)} has errors ({errors.Count}); first: {(errors.Count > 0 ? errors[0] : "unknown problem")}");

        var lastGood = file.ReadLastGood();
        if (lastGood is not null)
        {
            var fallback = SocialConfigToml.Read(lastGood.Text);
            if (fallback.IsValid)
            {
                file.AppliedHash = lastGood.Hash;
                file.Log($"Using {Path.GetFileName(file.LastGoodPath)} (from {lastGood.WrittenUtc:yyyy-MM-dd HH:mm} UTC) instead; {Path.GetFileName(file.Path)} was left as it is.");
                return new SocialListStartup(new SocialListLoadResult(fallback, isNew: false), true, lastGood.WrittenUtc, errors, hash);
            }
        }

        // Nothing better to use: the valid rows of the main file (if it could be read), with the problems to show.
        return new SocialListStartup(new SocialListLoadResult(parsed, isNew: false), false, null, errors, hash);
    }

    public static SettingsStartup LoadSettings(WatchedFile file)
    {
        var read = file.Detector.Load();
        if (read.Kind == FileChangeKind.Missing)
        {
            file.AppliedHash = null;
            return new SettingsStartup(new AppSettingsLoadResult(new AppSettings(), isNew: true), false, null, Array.Empty<string>(), null);
        }

        AppSettingsLoadResult parsed;
        if (read.Kind == FileChangeKind.Changed)
        {
            parsed = AppSettingsToml.Read(read.Text!);
            if (parsed.IsValid)
            {
                file.WriteLastGood(read.Text!);
                file.AppliedHash = read.Hash;
                return new SettingsStartup(parsed, false, null, Array.Empty<string>(), null);
            }
        }
        else
        {
            parsed = new AppSettingsLoadResult(new AppSettings(), isNew: false);
            parsed.Errors.Add(FileInUseMessage);
        }

        var errors = parsed.Errors.ToList();
        var hash = read.Hash;
        file.Log($"{Path.GetFileName(file.Path)} has errors ({errors.Count}); first: {(errors.Count > 0 ? errors[0] : "unknown problem")}");

        var lastGood = file.ReadLastGood();
        if (lastGood is not null)
        {
            var fallback = AppSettingsToml.Read(lastGood.Text);
            if (fallback.IsValid)
            {
                file.AppliedHash = lastGood.Hash;
                file.Log($"Using {Path.GetFileName(file.LastGoodPath)} (from {lastGood.WrittenUtc:yyyy-MM-dd HH:mm} UTC) instead; {Path.GetFileName(file.Path)} was left as it is.");
                return new SettingsStartup(fallback, true, lastGood.WrittenUtc, errors, hash);
            }
        }

        return new SettingsStartup(parsed, false, null, errors, hash);
    }
}
