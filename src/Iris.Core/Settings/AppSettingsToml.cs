using System.Globalization;
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
/// hosted_config_auto_check = false
/// hosted_config_last_check = "2026-10-22T09:30:00Z"   # optional; written as text, UTC
/// panel_enabled = true
/// panel_width_percent = 16       # 8 to 40
/// panel_text_size = 18           # 10 to 40, pixels at 1440p
/// panel_right_gap_percent = 10   # 0 to 40
/// panel_top_gap_percent = 15     # 0 to 60
/// nameplate_tracking_fps = 30    # 15 to 120: how often the brackets are moved (the screen is read 5 times a second regardless)
/// overlay_disabled = false       # "Disable Overlay": hide everything and stop screen reading
/// hotkey_toggle_overlay = ""     # e.g. "Ctrl+Shift+O"; empty = none
/// hotkey_toggle_panel = ""
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
        "hosted_config_auto_check", "hosted_config_last_check",
        "panel_enabled", "panel_width_percent", "panel_text_size", "panel_right_gap_percent", "panel_top_gap_percent",
        "nameplate_tracking_fps",
        "overlay_disabled", "hotkey_toggle_overlay", "hotkey_toggle_panel",
    };

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

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
        sb.AppendLine($"hosted_config_auto_check = {Bool(settings.HostedConfigAutoCheck)}");
        if (settings.HostedConfigLastCheck is { } lastCheck)
            sb.AppendLine($"hosted_config_last_check = {SocialConfigToml.Quote(lastCheck.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture))}");
        sb.AppendLine($"panel_enabled = {Bool(settings.PanelEnabled)}");
        sb.AppendLine($"panel_width_percent = {Math.Clamp(settings.PanelWidthPercent, AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax)}");
        sb.AppendLine($"panel_text_size = {Math.Clamp(settings.PanelTextSize, AppSettings.PanelTextSizeMin, AppSettings.PanelTextSizeMax)}");
        sb.AppendLine($"panel_right_gap_percent = {Math.Clamp(settings.PanelRightGapPercent, AppSettings.PanelRightGapPercentMin, AppSettings.PanelRightGapPercentMax)}");
        sb.AppendLine($"panel_top_gap_percent = {Math.Clamp(settings.PanelTopGapPercent, AppSettings.PanelTopGapPercentMin, AppSettings.PanelTopGapPercentMax)}");
        sb.AppendLine($"nameplate_tracking_fps = {Math.Clamp(settings.NameplateTrackingFps, AppSettings.NameplateTrackingFpsMin, AppSettings.NameplateTrackingFpsMax)}");
        sb.AppendLine($"overlay_disabled = {Bool(settings.OverlayDisabled)}");
        sb.AppendLine($"hotkey_toggle_overlay = {SocialConfigToml.Quote(HotkeyText.Normalize(settings.HotkeyToggleOverlay))}");
        sb.AppendLine($"hotkey_toggle_panel = {SocialConfigToml.Quote(HotkeyText.Normalize(settings.HotkeyTogglePanel))}");
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

        var autoCheck = ReadBool(root, "hosted_config_auto_check", defaults.HostedConfigAutoCheck, warnings);
        if (autoCheck && url.Length == 0)
        {
            // The automatic check needs an address to look at (the Settings tab unticks it when the address is cleared, too).
            autoCheck = false;
            warnings.Add("hosted_config_auto_check: needs a valid hosted_config_url; switched off.");
        }

        DateTimeOffset? lastCheck = defaults.HostedConfigLastCheck;
        if (root.TryGetValue("hosted_config_last_check", out var checkRaw))
        {
            switch (checkRaw)
            {
                case string checkText when checkText.Trim().Length == 0:
                    break; // empty means "never"
                case string checkText when TryParseTimestamp(checkText, out var parsed):
                    lastCheck = parsed;
                    break;
                case TomlDateTime tomlTime: // an unquoted TOML date-time, e.g. typed by hand: hosted_config_last_check = 2026-10-22T09:30:15Z
                    lastCheck = TruncateToSeconds(tomlTime.DateTime.ToUniversalTime());
                    break;
                case DateTimeOffset offsetValue:
                    lastCheck = TruncateToSeconds(offsetValue.ToUniversalTime());
                    break;
                case DateTime dateValue:
                    lastCheck = TruncateToSeconds(new DateTimeOffset(dateValue.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dateValue, DateTimeKind.Utc) : dateValue.ToUniversalTime()));
                    break;
                default:
                    warnings.Add("hosted_config_last_check: must be a date and time as text, like \"2026-10-22T09:30:00Z\"; ignored (treated as never checked).");
                    break;
            }
        }

        var panelEnabled = ReadBool(root, "panel_enabled", defaults.PanelEnabled, warnings);
        var panelWidth = ReadInt(root, "panel_width_percent", defaults.PanelWidthPercent,
            AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax, warnings);
        var panelText = ReadInt(root, "panel_text_size", defaults.PanelTextSize,
            AppSettings.PanelTextSizeMin, AppSettings.PanelTextSizeMax, warnings);
        var panelRightGap = ReadInt(root, "panel_right_gap_percent", defaults.PanelRightGapPercent,
            AppSettings.PanelRightGapPercentMin, AppSettings.PanelRightGapPercentMax, warnings);
        var panelTopGap = ReadInt(root, "panel_top_gap_percent", defaults.PanelTopGapPercent,
            AppSettings.PanelTopGapPercentMin, AppSettings.PanelTopGapPercentMax, warnings);

        var trackingFps = ReadInt(root, "nameplate_tracking_fps", defaults.NameplateTrackingFps,
            AppSettings.NameplateTrackingFpsMin, AppSettings.NameplateTrackingFpsMax, warnings);

        var overlayDisabled = ReadBool(root, "overlay_disabled", defaults.OverlayDisabled, warnings);
        var hotkeyOverlay = ReadHotkey(root, "hotkey_toggle_overlay", warnings);
        var hotkeyPanel = ReadHotkey(root, "hotkey_toggle_panel", warnings);

        var settings = new AppSettings
        {
            OverlayDisabled = overlayDisabled,
            HotkeyToggleOverlay = hotkeyOverlay,
            HotkeyTogglePanel = hotkeyPanel,
            PanelEnabled = panelEnabled,
            PanelWidthPercent = panelWidth,
            PanelTextSize = panelText,
            PanelRightGapPercent = panelRightGap,
            PanelTopGapPercent = panelTopGap,
            NameplateTrackingFps = trackingFps,
            CacheTtlDays = ttl,
            SessionLogEnabled = logEnabled,
            CheckForUpdates = checkUpdates,
            HostedConfigUrl = url,
            HostedConfigLastHash = hash,
            HostedConfigAutoCheck = autoCheck,
            HostedConfigLastCheck = lastCheck,
        };
        var result = new AppSettingsLoadResult(settings, isNew: false);
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>Parses an ISO 8601 date and time; a text without a zone is taken as UTC. The result is UTC, in whole seconds.</summary>
    public static bool TryParseTimestamp(string? text, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = TruncateToSeconds(parsed);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>The same moment without fractions of a second (that is all the settings file keeps), as UTC.</summary>
    public static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
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

    /// <summary>Reads a whole number that must lie in min..max (both included); anything else is a warning and the default.</summary>
    private static int ReadInt(TomlTable root, string key, int fallback, int min, int max, List<string> warnings)
    {
        if (!root.TryGetValue(key, out var raw))
            return fallback;
        if (raw is long value && value >= min && value <= max)
            return (int)value;
        warnings.Add($"{key}: must be a whole number from {min} to {max}; using {fallback}.");
        return fallback;
    }

    /// <summary>Reads a hotkey text (normalised). Empty or missing is "no hotkey"; anything invalid is a warning and no hotkey.</summary>
    private static string ReadHotkey(TomlTable root, string key, List<string> warnings)
    {
        if (!root.TryGetValue(key, out var raw))
            return string.Empty;
        if (raw is not string text)
        {
            warnings.Add($"{key}: must be text like \"Ctrl+Shift+O\"; no hotkey is set.");
            return string.Empty;
        }
        if (text.Trim().Length == 0)
            return string.Empty;
        if (HotkeyText.TryParse(text, out var hotkey, out var problem))
            return HotkeyText.Format(hotkey!);
        warnings.Add($"{key}: {problem} No hotkey is set.");
        return string.Empty;
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
