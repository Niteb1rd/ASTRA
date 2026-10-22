using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core;
using Iris.Core.Cache;
using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Logging;
using Iris.Core.Rsi;
using Iris.Core.Settings;
using Iris.Core.Storage;

namespace Iris.App.Services;

/// <summary>What <see cref="AppServices.ApplyPoll"/> did, so the config window can bring itself up to date.</summary>
public sealed class ConfigChanges
{
    public ConfigChanges(bool listChanged, bool settingsChanged, bool exclusionsChanged, bool errorsChanged, IReadOnlyList<string> messages)
    {
        ListChanged = listChanged;
        SettingsChanged = settingsChanged;
        ExclusionsChanged = exclusionsChanged;
        ErrorsChanged = errorsChanged;
        Messages = messages;
    }

    /// <summary>A new valid list is now the current one: the window must show its rows (unsaved list edits are discarded).</summary>
    public bool ListChanged { get; }

    /// <summary>New valid settings are now in use: the window must show them (unsaved settings edits are discarded).</summary>
    public bool SettingsChanged { get; }

    /// <summary>The exclusion list was reloaded.</summary>
    public bool ExclusionsChanged { get; }

    /// <summary>A file got errors, or its errors went away: the window must refresh its banners.</summary>
    public bool ErrorsChanged { get; }

    /// <summary>What to tell the user (also sent to the tray as a balloon).</summary>
    public IReadOnlyList<string> Messages { get; }

    public bool Any => ListChanged || SettingsChanged || ExclusionsChanged || ErrorsChanged || Messages.Count > 0;

    /// <summary>All messages as one line, or null if there are none.</summary>
    public string? Message => Messages.Count == 0 ? null : string.Join(" ", Messages);
}

/// <summary>
/// Everything the app shares: the data folder, the log, the cache database, the exclusion list, the ONE RSI client
/// (with its one rate limiter) and the list the user saved. Windows and the tray get what they need from here.
/// It also owns the watched config files: it looks at them (<see cref="PollConfigFiles"/>), applies what it finds
/// (<see cref="ApplyPoll"/>) and remembers which of them has errors right now.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _hostedHttp;
    private readonly HostedConfigClient _hostedClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _pollGate = new();
    private bool _isShuttingDown;

    private AppServices(
        DataPaths paths, ILog log, FileLog fileLog, CacheDatabase database, ExclusionList exclusions, HttpClient http,
        HttpClient hostedHttp, IRsiClient rsi, WatchedFile listFile, WatchedFile settingsFile,
        SocialListStartup listStartup, SettingsStartup settingsStartup)
    {
        Paths = paths;
        Log = log;
        FileLog = fileLog;
        Database = database;
        Exclusions = exclusions;
        _http = http;
        _hostedHttp = hostedHttp;
        _hostedClient = new HostedConfigClient(hostedHttp, Clock, log);
        Rsi = rsi;
        ListFile = listFile;
        SettingsFile = settingsFile;
        ListStartup = listStartup;
        SettingsStartup = settingsStartup;
        ListLoad = listStartup.Load;
        CurrentConfig = listStartup.Load.Config;
        SettingsLoad = settingsStartup.Load;
        Settings = settingsStartup.Load.Settings;

        // A file with errors at startup: remember why, for the banner in the config window.
        if (listStartup.MainFileHasErrors)
        {
            var note = listStartup.UsedLastGood
                ? $"IRIS is using the last good version (from {FormatTime(listStartup.LastGoodTime!.Value)})."
                : "Rows with problems are left out of the lists below. Nothing is written until you press Save.";
            SetListErrors(listStartup.MainFileErrors, listStartup.MainFileHash, note);
        }
        if (settingsStartup.MainFileHasErrors)
        {
            var note = settingsStartup.UsedLastGood
                ? $"IRIS is using the last good version (from {FormatTime(settingsStartup.LastGoodTime!.Value)})."
                : "IRIS is using the default settings until you press Save.";
            SetSettingsErrors(settingsStartup.MainFileErrors, settingsStartup.MainFileHash, note);
        }

        CacheSettings = new CacheSettings(Settings.CacheTtlDays);
        Validator = new RowValidator(database, rsi, Clock, CacheSettings, log);
        Refresher = new CacheRefresher(database, rsi, Clock, CacheSettings, log);
    }

    public IClock Clock => SystemClock.Instance;

    public DataPaths Paths { get; }

    /// <summary>Everything written here goes to the in-memory log and to today's log file.</summary>
    public ILog Log { get; }

    /// <summary>The daily log file writer (for its folder and today's file path).</summary>
    public FileLog FileLog { get; }

    public CacheDatabase Database { get; }

    public ExclusionList Exclusions { get; }

    /// <summary>The shared RSI client. Everything that talks to RSI uses this one (it owns the pacing and backoff rules).</summary>
    public IRsiClient Rsi { get; }

    /// <summary>The refresh interval, shared with the validator and the refresher. Changes at once when the setting is saved.</summary>
    public CacheSettings CacheSettings { get; }

    /// <summary>The settings of this PC as last saved (or as loaded). Replaced, never changed in place.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>The settings that were loaded at startup (the file, its last-good copy, or the defaults). Problems are mentioned once.</summary>
    public AppSettingsLoadResult SettingsLoad { get; }

    /// <summary>The list file, its change detector and its last-good copy.</summary>
    public WatchedFile ListFile { get; }

    /// <summary>The settings file, its change detector and its last-good copy.</summary>
    public WatchedFile SettingsFile { get; }

    /// <summary>How the list file was loaded at startup (including whether its last-good copy had to be used).</summary>
    public SocialListStartup ListStartup { get; }

    /// <summary>How the settings file was loaded at startup.</summary>
    public SettingsStartup SettingsStartup { get; }

    /// <summary>What is wrong with social-overlay.toml right now (as it is on disk). Empty when the file is fine.</summary>
    public IReadOnlyList<string> ListFileErrors { get; private set; } = Array.Empty<string>();

    /// <summary>One line for the banner: which version IRIS is using meanwhile.</summary>
    public string ListFileNote { get; private set; } = string.Empty;

    /// <summary>Hash of the bad version of the list file, or null (unknown / no errors).</summary>
    public string? ListFileErrorHash { get; private set; }

    public bool ListFileHasErrors => ListFileErrors.Count > 0;

    /// <summary>What is wrong with settings.toml right now. Empty when the file is fine.</summary>
    public IReadOnlyList<string> SettingsFileErrors { get; private set; } = Array.Empty<string>();

    public string SettingsFileNote { get; private set; } = string.Empty;

    public string? SettingsFileErrorHash { get; private set; }

    public bool SettingsFileHasErrors => SettingsFileErrors.Count > 0;

    /// <summary>
    /// True while settings.toml has errors (at startup, or after an outside edit) and has not been replaced since: the file is then
    /// left alone (a background change must not overwrite what the user may want to repair by hand), and the next Save must write
    /// the file even when no setting was edited.
    /// </summary>
    public bool SettingsFileNeedsSave => SettingsFileHasErrors;

    /// <summary>
    /// Called with a short sentence when something happened that the user should hear about while IRIS sits in the tray
    /// (the app shows it as a balloon). May be null. Always called on the UI thread.
    /// </summary>
    public Action<string>? Notify { get; set; }

    /// <summary>Raised on the UI thread after a new list became the current one (imported, or loaded from an edited file).</summary>
    public event EventHandler? ListApplied;

    /// <summary>Cancelled when IRIS quits. Everything long-running should stop when it is.</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>True once IRIS started quitting.</summary>
    public bool IsShuttingDown => _isShuttingDown;

    public RowValidator Validator { get; }

    public CacheRefresher Refresher { get; }

    /// <summary>What the list file looked like at startup (errors and warnings to show once). Replaced after a successful import.</summary>
    public SocialListLoadResult ListLoad { get; private set; }

    /// <summary>The list as last saved (or as loaded). Replaced, never changed in place, when the user saves.</summary>
    public SocialConfig CurrentConfig { get; private set; }

    /// <summary>Remembered for the overlay increment; nothing reads it yet.</summary>
    public bool IsOverlayPaused { get; set; }

    /// <summary>Opens everything under the Data folder next to the program. Throws if the cache file cannot be opened.</summary>
    public static AppServices Create(string exeDirectory)
    {
        var paths = DataPaths.FromBaseDirectory(exeDirectory);
        paths.EnsureCreated();

        var clock = SystemClock.Instance;

        // The log file stays off until settings.toml has been read: the settings decide whether it is written at all.
        // Old log files are cleaned up either way (switching the log off does not leave 30 days of files behind forever).
        FileLog.DeleteOlderThan(paths.LogsFolder, TimeSpan.FromDays(30), clock.UtcNow);
        var fileLog = new FileLog(paths.LogsFolder, clock) { Enabled = false };
        var log = new TeeLog(new InMemoryLog(), fileLog);

        var settingsFile = new WatchedFile(paths.SettingsFile, paths.SettingsLastGoodFile, clock, log);
        var settingsStartup = ConfigStartup.LoadSettings(settingsFile);
        var settingsLoad = settingsStartup.Load;
        fileLog.Enabled = settingsLoad.Settings.SessionLogEnabled;

        foreach (var error in settingsStartup.MainFileErrors)
            log.Write(clock.UtcNow, settingsStartup.UsedLastGood
                ? "settings.toml has errors, the last good copy is used instead: " + error
                : "settings.toml could not be used, default settings apply: " + error);
        foreach (var warning in settingsLoad.Warnings)
            log.Write(clock.UtcNow, "settings.toml: " + warning);

        var database = CacheDatabase.OpenFile(paths.CacheFile);
        HttpClient? http = null;
        HttpClient? hostedHttp = null;
        try
        {
            var exclusions = ExclusionList.LoadOrCreate(paths.ExclusionsFile, clock, log);

            var listFile = new WatchedFile(paths.SocialListFile, paths.SocialListLastGoodFile, clock, log);
            var listStartup = ConfigStartup.LoadSocialList(listFile);
            foreach (var error in listStartup.MainFileErrors.Take(10))
                log.Write(clock.UtcNow, listStartup.UsedLastGood
                    ? "social-overlay.toml has errors, the last good copy is used instead: " + error
                    : "social-overlay.toml has errors: " + error);

            http = RsiClient.CreateHttpClient();
            var limiter = new RsiRateLimiter(clock);
            var rsi = new RsiClient(http, limiter, clock, log);
            hostedHttp = HostedConfigClient.CreateHttpClient();

            return new AppServices(
                paths, log, fileLog, database, exclusions, http, hostedHttp, rsi, listFile, settingsFile, listStartup, settingsStartup);
        }
        catch
        {
            hostedHttp?.Dispose();
            http?.Dispose();
            database.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Watching the config files
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Looks at social-overlay.toml, settings.toml and exclusions.txt and says what changed. Reads and parses, and keeps the last-good
    /// copies up to date, but applies nothing: that is <see cref="ApplyPoll"/>. Safe to call from a background thread; calls run one
    /// after the other.
    /// </summary>
    public ConfigPoll PollConfigFiles()
    {
        lock (_pollGate)
            return ConfigPoller.PollAll(ListFile, SettingsFile, Exclusions);
    }

    /// <summary>
    /// Applies what <see cref="PollConfigFiles"/> found: a valid new list or settings become current, a file with errors is remembered
    /// (the previous version stays in use), the user is told through <see cref="Notify"/>. Call it on the UI thread.
    /// </summary>
    public ConfigChanges ApplyPoll(ConfigPoll poll)
    {
        var messages = new List<string>();
        var listChanged = false;
        var settingsChanged = false;
        var errorsChanged = false;

        var list = poll.List;
        switch (list.Kind)
        {
            case PollKind.Applied when list.Hash != ListFile.OwnWriteHash:
                CurrentConfig = list.Config!;
                ListLoad = new SocialListLoadResult(list.Load!, isNew: false);
                errorsChanged |= ClearListErrors();
                listChanged = true;
                messages.Add(ConfigMessages.NewConfigLoaded(CurrentConfig));
                Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName} was changed from outside and has been loaded.");
                foreach (var warning in list.Warnings.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName}: {warning}");
                break;

            case PollKind.Applied: // IRIS's own save that raced the check: nothing new
            case PollKind.Restored:
                errorsChanged |= ClearListErrors();
                break;

            case PollKind.Rejected:
                SetListErrors(list.Errors, list.Hash, "IRIS is still using the previous version.");
                errorsChanged = true;
                messages.Add(ConfigMessages.StillUsingPrevious(DataPaths.SocialListFileName));
                Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName} was changed from outside but has {list.Errors.Count} error(s); still using the previous version.");
                foreach (var error in list.Errors.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName}: {error}");
                break;

            case PollKind.Missing:
                Log.Write(Clock.UtcNow, $"{DataPaths.SocialListFileName} is missing; the list in memory stays in use (saving writes the file again).");
                break;
        }

        var settings = poll.Settings;
        switch (settings.Kind)
        {
            case PollKind.Applied when settings.Hash != SettingsFile.OwnWriteHash:
                ApplySettings(settings.Settings!);
                errorsChanged |= ClearSettingsErrors();
                settingsChanged = true;
                messages.Add(ConfigMessages.NewSettingsLoaded);
                Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName} was changed from outside and has been loaded.");
                foreach (var warning in settings.Warnings.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName}: {warning}");
                break;

            case PollKind.Applied:
            case PollKind.Restored:
                errorsChanged |= ClearSettingsErrors();
                break;

            case PollKind.Rejected:
                SetSettingsErrors(settings.Errors, settings.Hash, "IRIS is still using the previous settings.");
                errorsChanged = true;
                messages.Add(ConfigMessages.StillUsingPrevious(DataPaths.SettingsFileName));
                Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName} was changed from outside but has {settings.Errors.Count} error(s); still using the previous settings.");
                foreach (var error in settings.Errors.Take(5))
                    Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName}: {error}");
                break;

            case PollKind.Missing:
                Log.Write(Clock.UtcNow, $"{DataPaths.SettingsFileName} is missing; the settings in memory stay in use (saving writes the file again).");
                break;
        }

        var exclusionsChanged = poll.ExclusionsReloaded;
        if (exclusionsChanged)
        {
            messages.Add(ConfigMessages.ExclusionsReloaded(poll.ExclusionCount));
            Log.Write(Clock.UtcNow, $"Exclusion list reloaded from {DataPaths.ExclusionsFileName} ({poll.ExclusionCount} names).");
        }

        var changes = new ConfigChanges(listChanged, settingsChanged, exclusionsChanged, errorsChanged, messages);
        if (changes.Message is { } text)
            Notify?.Invoke(text);
        if (listChanged)
            ListApplied?.Invoke(this, EventArgs.Empty);
        return changes;
    }

    private void SetListErrors(IReadOnlyList<string> errors, string? hash, string note)
    {
        ListFileErrors = errors;
        ListFileErrorHash = hash;
        ListFileNote = note;
    }

    private bool ClearListErrors()
    {
        var had = ListFileHasErrors;
        ListFileErrors = Array.Empty<string>();
        ListFileErrorHash = null;
        ListFileNote = string.Empty;
        return had;
    }

    private void SetSettingsErrors(IReadOnlyList<string> errors, string? hash, string note)
    {
        SettingsFileErrors = errors;
        SettingsFileErrorHash = hash;
        SettingsFileNote = note;
    }

    private bool ClearSettingsErrors()
    {
        var had = SettingsFileHasErrors;
        SettingsFileErrors = Array.Empty<string>();
        SettingsFileErrorHash = null;
        SettingsFileNote = string.Empty;
        return had;
    }

    private static string FormatTime(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Called after the list file was written successfully (a save in the config window, or an import): this is the current list from now on,
    /// and the file no longer has errors (the write replaced it).
    /// </summary>
    public void SetCurrentConfig(SocialConfig config)
    {
        CurrentConfig = config;
        ClearListErrors();
    }

    /// <summary>
    /// Saves the settings file and, only if that worked, applies them at once: the refresh interval and the log-file switch
    /// take effect without a restart. Returns the save result (nothing changes when it failed).
    /// </summary>
    public SaveResult SaveSettings(AppSettings settings)
    {
        var saved = AppSettingsToml.Save(Paths.SettingsFile, settings, SettingsFile);
        if (saved.Success)
        {
            ClearSettingsErrors(); // the user's own save replaced the unreadable file (the old one stays as settings.toml.bak)
            ApplySettings(settings);
        }
        else
            Log.Write(Clock.UtcNow, "Settings not saved: " + saved.Error);
        return saved;
    }

    /// <summary>
    /// Saves a changed copy of the current settings (for example the hosted config hash or address, after an import).
    /// If settings.toml has errors (at startup, or after an outside edit) the file is NOT touched: the change only lives in memory (and in the log)
    /// until the user presses Save in the Settings tab, which writes the file.
    /// </summary>
    public SaveResult UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var changed = change(Settings);
        if (!SettingsFileHasErrors)
            return SaveSettings(changed);

        ApplySettings(changed);
        Log.Write(Clock.UtcNow,
            "settings.toml has errors, so it was not overwritten; the hosted config address/version is kept in memory only until you save in the Settings tab.");
        return SaveResult.Ok();
    }

    private void ApplySettings(AppSettings settings)
    {
        var ttl = settings.CacheTtlDays == 7 ? 7 : 30;
        if (CacheSettings.TtlDays != ttl)
            Log.Write(Clock.UtcNow, $"Cache refresh interval is now {ttl} days");

        var logWasOn = FileLog.Enabled;
        if (logWasOn && !settings.SessionLogEnabled)
            Log.Write(Clock.UtcNow, "Log file switched off (setting); this is the last line written to it");
        FileLog.Enabled = settings.SessionLogEnabled;
        if (!logWasOn && settings.SessionLogEnabled)
            Log.Write(Clock.UtcNow, "Log file switched on (setting)");

        CacheSettings.TtlDays = ttl;
        Settings = settings;
    }

    /// <summary>Downloads the user's hosted config file (see <see cref="HostedConfigClient"/>). Stops when IRIS quits.</summary>
    public Task<HostedConfigResult> FetchHostedConfigAsync(string url) => _hostedClient.FetchAsync(url, _shutdown.Token);

    /// <summary>
    /// Writes an imported config to the list file in the chosen mode. On success the app's current config becomes the result and the
    /// startup load result is replaced, so the window does not treat the file as damaged any more; <see cref="ListApplied"/> is raised
    /// (the fill-missing refresh starts from it).
    /// </summary>
    public ImportResult ApplyImport(SocialConfig imported, ImportMode mode)
    {
        var result = ConfigImporter.Import(Paths.SocialListFile, imported, mode, Clock, ListFile);
        if (result.Success && result.Config is not null)
        {
            ListLoad = new SocialListLoadResult(new ConfigLoadResult { Config = result.Config }, isNew: false);
            SetCurrentConfig(result.Config);
            Log.Write(Clock.UtcNow, $"Import ({mode}): {result.Summary}");
            ListApplied?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Log.Write(Clock.UtcNow, $"Import ({mode}) failed: {result.Error}");
        }
        return result;
    }

    /// <summary>
    /// The full refresh ("Refresh Cache (long time)"): refetches every entry of the saved list from RSI, whatever its age.
    /// Slow: one request per second at best. Returns a one-paragraph summary; throws <see cref="OperationCanceledException"/> when IRIS quits.
    /// </summary>
    public async Task<string> RefreshAllAsync()
    {
        var config = CurrentConfig;
        if (config.Players.Count == 0 && config.Orgs.Count == 0)
            return "Nothing to refresh: your player and org lists are empty.";

        var token = _shutdown.Token;
        var summary = await Task.Run(() => Refresher.RefreshAllAsync(config, token)).ConfigureAwait(true);
        return Describe("Refreshed", summary);
    }

    /// <summary>
    /// The background "fill missing" run: fetches only what the saved list has no fresh cache entry for.
    /// <paramref name="ct"/> is linked to nothing else; the caller links it to <see cref="ShutdownToken"/>.
    /// </summary>
    public Task<RefreshSummary> FillMissingAsync(SocialConfig config, CancellationToken ct) =>
        Task.Run(() => Refresher.RefreshMissingOrStaleAsync(config, ct), ct);

    /// <summary>"Refreshed 3 players and 1 org. Not found on RSI: 1. Could not update: 2."</summary>
    public static string Describe(string verb, RefreshSummary summary)
    {
        var parts = new List<string>
        {
            $"{verb} {Count(summary.PlayersRefreshed, "player")} and {Count(summary.OrgsRefreshed, "org")}.",
        };
        var notFound = summary.PlayersNotFound + summary.OrgsNotFound;
        if (notFound > 0)
            parts.Add($"Not found on RSI: {notFound}.");
        if (summary.Failures.Count > 0)
            parts.Add($"Could not update: {summary.Failures.Count}.");
        return string.Join(" ", parts);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>Tells everything that is running (an RSI refresh, a hosted config download) to stop. Dispose does the same.</summary>
    public void RequestShutdown()
    {
        _isShuttingDown = true;
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _isShuttingDown = true;
        _shutdown.Cancel();
        _http.Dispose();
        _hostedHttp.Dispose();
        Database.Dispose();
        _shutdown.Dispose();
    }
}
