using System.Text;
using Iris.Core.Config;
using Iris.Core.Storage;
using Tomlyn;
using Tomlyn.Model;

namespace Iris.Core.Settings;

/// <summary>What <see cref="AppSettingsToml"/> found. <see cref="Settings"/> is never null: problems give defaults.</summary>
public sealed class AppSettingsLoadResult
{
    public AppSettingsLoadResult(AppSettings settings, bool isNew)
    {
        Settings = settings;
        IsNew = isNew;
    }

    /// <summary>The settings to use. Defaults when the file is missing or unreadable.</summary>
    public AppSettings Settings { get; }

    /// <summary>True when there was no file yet (defaults are used; nothing is written until the user saves).</summary>
    public bool IsNew { get; }

    /// <summary>Problems that made the whole file unusable (bad TOML, unsupported version, unreadable). Defaults are used.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Non-fatal notes (unknown keys, a value that was replaced by its default).</summary>
    public List<string> Warnings { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reads and writes <c>Data\settings.toml</c>.
///
/// Example file:
/// <code>
/// format_version = 1
/// cache_ttl_days = 30
/// session_log_enabled = true
/// check_for_updates = true
/// hosted_config_url = ""
/// hosted_config_last_hash = ""
/// </code>
/// Rules: a missing file means defaults (and nothing is written until the user saves); unknown keys are warnings;
/// a value of the wrong type or out of range is a warning and that one setting falls back to its default;
/// a file that is not TOML at all, or is from a newer version, is an error and every setting is the default.
/// </summary>
public static class AppSettingsToml
{
    private static readonly string[] KnownKeys =
    {
        "format_version", "cache_ttl_days", "session_log_enabled", "check_for_updates", "hosted_config_url", "hosted_config_last_hash",
    };

    // ------------------------------------------------------------------ write

    public static string Write(AppSettings settings)
    {
        var ttl = settings.CacheTtlDays == 7 ? 7 : 30;
        var sb = new StringBuilder();
        sb.AppendLine("# IRIS settings for this PC. Not part of the shareable list file; never exported.");
        sb.AppendLine($"format_version = {AppSettings.FormatVersion}");
        sb.AppendLine($"cache_ttl_days = {ttl}");
        sb.AppendLine($"session_log_enabled = {Bool(settings.SessionLogEnabled)}");
        sb.AppendLine($"check_for_updates = {Bool(settings.CheckForUpdates)}");
        sb.AppendLine($"hosted_config_url = {SocialConfigToml.Quote(settings.HostedConfigUrl ?? string.Empty)}");
        sb.AppendLine($"hosted_config_last_hash = {SocialConfigToml.Quote(settings.HostedConfigLastHash ?? string.Empty)}");
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    // ------------------------------------------------------------------ read

    public static AppSettingsLoadResult Read(string text)
    {
        var doc = Toml.Parse(text);
        if (doc.HasErrors)
        {
            var bad = new AppSettingsLoadResult(new AppSettings(), isNew: false);
            foreach (var d in doc.Diagnostics)
                bad.Errors.Add("TOML syntax: " + d);
            return bad;
        }

        TomlTable root;
        try
        {
            root = Toml.ToModel(doc);
        }
        catch (Exception ex)
        {
            var bad = new AppSettingsLoadResult(new AppSettings(), isNew: false);
            bad.Errors.Add("TOML structure: " + ex.Message);
            return bad;
        }

        // Everything is collected first so that an error leaves the defaults untouched.
        var warnings = new List<string>();
        var errors = new List<string>();

        if (root.TryGetValue("format_version", out var versionRaw))
        {
            if (versionRaw is not long version)
                errors.Add("format_version: must be a whole number.");
            else if (version < 1 || version > AppSettings.FormatVersion)
                errors.Add($"format_version: {version} is not supported (this app reads version {AppSettings.FormatVersion}).");
        }

        foreach (var key in root.Keys)
            if (!KnownKeys.Contains(key))
                warnings.Add($"{key}: unknown setting, ignored.");

        if (errors.Count > 0)
        {
            var failed = new AppSettingsLoadResult(new AppSettings(), isNew: false);
            failed.Errors.AddRange(errors);
            failed.Warnings.AddRange(warnings);
            return failed;
        }

        var defaults = new AppSettings();
        var ttl = defaults.CacheTtlDays;
        if (root.TryGetValue("cache_ttl_days", out var ttlRaw))
        {
            if (ttlRaw is long days && (days == 7 || days == 30))
                ttl = (int)days;
            else
                warnings.Add("cache_ttl_days: must be 30 or 7; using 30.");
        }

        var logEnabled = ReadBool(root, "session_log_enabled", defaults.SessionLogEnabled, warnings);
        var checkUpdates = ReadBool(root, "check_for_updates", defaults.CheckForUpdates, warnings);

        var url = defaults.HostedConfigUrl;
        if (root.TryGetValue("hosted_config_url", out var urlRaw))
        {
            if (urlRaw is not string urlText)
            {
                warnings.Add("hosted_config_url: must be text; ignored.");
            }
            else if (urlText.Trim().Length > 0)
            {
                var problem = HostedConfigUrl.Check(urlText);
                if (problem is null)
                    url = urlText.Trim();
                else
                    warnings.Add("hosted_config_url: " + problem + " The address is ignored.");
            }
        }

        var hash = defaults.HostedConfigLastHash;
        if (root.TryGetValue("hosted_config_last_hash", out var hashRaw))
        {
            if (hashRaw is string hashText)
            {
                var candidate = hashText.Trim().ToLowerInvariant();
                if (candidate.Length == 0)
                    hash = string.Empty;
                else if (IsValidHash(candidate))
                    hash = candidate;
                else
                    warnings.Add("hosted_config_last_hash: must be 64 hex characters (a SHA-256); cleared, so the hosted config will be offered again.");
            }
            else
                warnings.Add("hosted_config_last_hash: must be text; ignored.");
        }

        var settings = new AppSettings
        {
            CacheTtlDays = ttl,
            SessionLogEnabled = logEnabled,
            CheckForUpdates = checkUpdates,
            HostedConfigUrl = url,
            HostedConfigLastHash = hash,
        };
        var result = new AppSettingsLoadResult(settings, isNew: false);
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>True for a SHA-256 written as 64 hex characters (either case).</summary>
    public static bool IsValidHash(string? text)
    {
        if (text is null || text.Length != 64)
            return false;
        foreach (var c in text)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static bool ReadBool(TomlTable root, string key, bool fallback, List<string> warnings)
    {
        if (!root.TryGetValue(key, out var raw))
            return fallback;
        if (raw is bool value)
            return value;
        warnings.Add($"{key}: must be true or false; using {Bool(fallback)}.");
        return fallback;
    }

    // ------------------------------------------------------------------ files

    /// <summary>Reads the settings file. A missing file gives the defaults, marked as new. Never changes the file.</summary>
    public static AppSettingsLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettingsLoadResult(new AppSettings(), isNew: true);

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var unreadable = new AppSettingsLoadResult(new AppSettings(), isNew: false);
            unreadable.Errors.Add("The file could not be read: " + ex.Message);
            return unreadable;
        }

        return Read(text);
    }

    /// <summary>
    /// Writes the settings safely (tmp file, then replace; the previous version stays as settings.toml.bak).
    /// With <paramref name="watched"/> the save is remembered (not an outside edit) and becomes the last-good copy.
    /// </summary>
    public static SaveResult Save(string path, AppSettings settings, WatchedFile? watched = null)
    {
        var text = Write(settings);

        // Safety net: never replace the user's file with something this program cannot read back.
        var check = Read(text);
        if (!check.IsValid)
            return SaveResult.Failed("The settings are not valid and were not saved: " + string.Join(" ", check.Errors));

        try
        {
            AtomicFile.WriteText(path, text, keepBackup: true);
            watched?.Wrote(text);
            return SaveResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveResult.Failed("The settings could not be saved: " + ex.Message);
        }
    }
}
