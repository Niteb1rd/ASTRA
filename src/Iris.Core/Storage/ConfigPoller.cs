using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Settings;

namespace Iris.Core.Storage;

/// <summary>What one look at a watched file found.</summary>
public enum PollKind
{
    /// <summary>Nothing to do (unchanged, still locked, or IRIS's own save).</summary>
    Nothing,

    /// <summary>The file changed and is valid: use it. (Its last-good copy has been written.)</summary>
    Applied,

    /// <summary>The file changed back to exactly the version already in use (for example after the user fixed a typo): nothing to apply, but any error message is gone.</summary>
    Restored,

    /// <summary>The file changed and has errors: keep using the current version and tell the user. Each bad version is reported once.</summary>
    Rejected,

    /// <summary>The file is gone (reported once). The current version stays in use.</summary>
    Missing,
}

/// <summary>The outcome for the list file.</summary>
public sealed class SocialListPoll
{
    internal SocialListPoll(PollKind kind, ConfigLoadResult? load = null, string? hash = null, IReadOnlyList<string>? errors = null)
    {
        Kind = kind;
        Load = load;
        Hash = hash;
        Errors = errors ?? Array.Empty<string>();
    }

    public PollKind Kind { get; }

    /// <summary>The parsed file (Applied, Restored and Rejected).</summary>
    public ConfigLoadResult? Load { get; }

    /// <summary>The new valid list. Only set for Applied.</summary>
    public SocialConfig? Config => Kind == PollKind.Applied ? Load?.Config : null;

    public string? Hash { get; }

    /// <summary>What is wrong with the file (Rejected), each with its line number where the TOML parser gave one.</summary>
    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings => Load?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>();
}

/// <summary>The outcome for the settings file.</summary>
public sealed class SettingsPoll
{
    internal SettingsPoll(PollKind kind, AppSettingsLoadResult? load = null, string? hash = null, IReadOnlyList<string>? errors = null)
    {
        Kind = kind;
        Load = load;
        Hash = hash;
        Errors = errors ?? Array.Empty<string>();
    }

    public PollKind Kind { get; }

    public AppSettingsLoadResult? Load { get; }

    /// <summary>The new valid settings. Only set for Applied.</summary>
    public AppSettings? Settings => Kind == PollKind.Applied ? Load?.Settings : null;

    public string? Hash { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings => Load?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>();
}

/// <summary>Everything one look at the three watched files found. Nothing has been applied yet; the app does that on its UI thread.</summary>
public sealed class ConfigPoll
{
    public ConfigPoll(SocialListPoll list, SettingsPoll settings, bool exclusionsReloaded, int exclusionCount)
    {
        List = list;
        Settings = settings;
        ExclusionsReloaded = exclusionsReloaded;
        ExclusionCount = exclusionCount;
    }

    public SocialListPoll List { get; }

    public SettingsPoll Settings { get; }

    /// <summary>The exclusion list was re-read because exclusions.txt changed.</summary>
    public bool ExclusionsReloaded { get; }

    public int ExclusionCount { get; }

    public bool IsEmpty =>
        List.Kind == PollKind.Nothing && Settings.Kind == PollKind.Nothing && !ExclusionsReloaded;
}

/// <summary>
/// Looks at the watched config files and decides what each change means. Safe to run on a background thread (it only reads files,
/// parses, and writes last-good copies); the caller applies the result on its UI thread.
/// </summary>
public static class ConfigPoller
{
    public static ConfigPoll PollAll(WatchedFile listFile, WatchedFile settingsFile, ExclusionList exclusions)
    {
        var list = PollSocialList(listFile);
        var settings = PollSettings(settingsFile);
        var reloaded = exclusions.ReloadIfChanged();
        return new ConfigPoll(list, settings, reloaded, exclusions.Count);
    }

    public static SocialListPoll PollSocialList(WatchedFile file)
    {
        var change = file.Detector.Check();
        switch (change.Kind)
        {
            case FileChangeKind.Missing:
                return new SocialListPoll(PollKind.Missing);

            case FileChangeKind.Changed:
                var parsed = SocialConfigToml.Read(change.Text!);
                if (!parsed.IsValid)
                    return new SocialListPoll(PollKind.Rejected, parsed, change.Hash, parsed.Errors.ToList());

                if (change.Hash == file.OwnWriteHash || change.Hash == file.AppliedHash)
                    return new SocialListPoll(PollKind.Restored, parsed, change.Hash);

                file.WriteLastGood(change.Text!);
                file.AppliedHash = change.Hash;
                return new SocialListPoll(PollKind.Applied, parsed, change.Hash);

            default:
                return new SocialListPoll(PollKind.Nothing);
        }
    }

    public static SettingsPoll PollSettings(WatchedFile file)
    {
        var change = file.Detector.Check();
        switch (change.Kind)
        {
            case FileChangeKind.Missing:
                return new SettingsPoll(PollKind.Missing);

            case FileChangeKind.Changed:
                var parsed = AppSettingsToml.Read(change.Text!);
                if (!parsed.IsValid)
                    return new SettingsPoll(PollKind.Rejected, parsed, change.Hash, parsed.Errors.ToList());

                if (change.Hash == file.OwnWriteHash || change.Hash == file.AppliedHash)
                    return new SettingsPoll(PollKind.Restored, parsed, change.Hash);

                file.WriteLastGood(change.Text!);
                file.AppliedHash = change.Hash;
                return new SettingsPoll(PollKind.Applied, parsed, change.Hash);

            default:
                return new SettingsPoll(PollKind.Nothing);
        }
    }
}

/// <summary>The sentences shown to the user (tray balloon and window footer) when a watched file changes.</summary>
public static class ConfigMessages
{
    public static string NewConfigLoaded(SocialConfig config) =>
        $"New config loaded: {Count(config.Players.Count, "player")}, {Count(config.Orgs.Count, "org")}, {Count(config.OrgRules.Count, "rule")}";

    public const string NewSettingsLoaded = "New settings loaded";

    public static string ExclusionsReloaded(int names) => $"Exclusion list reloaded ({Count(names, "name")})";

    public static string StillUsingPrevious(string fileName) =>
        $"{fileName} has errors — still using the previous version. Open the config window for details.";

    public static string UsingLastGood(string fileName, DateTimeOffset lastGoodUtc) =>
        $"{fileName} has errors — using the last good version (from {lastGoodUtc.ToLocalTime():yyyy-MM-dd HH:mm})";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
