using System;
using System.Collections.Generic;
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

/// <summary>
/// Everything the app shares: the data folder, the log, the cache database, the exclusion list, the ONE RSI client
/// (with its one rate limiter) and the list the user saved. Windows and the tray get what they need from here.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _hostedHttp;
    private readonly HostedConfigClient _hostedClient;
    private readonly CancellationTokenSource _shutdown = new();

    // True while settings.toml exists but could not be read at startup and the user has not saved settings since:
    // the file is then left alone (a background change must not overwrite what the user may want to repair by hand).
    private bool _settingsFileUnreadable;

    private AppServices(
        DataPaths paths, ILog log, FileLog fileLog, CacheDatabase database, ExclusionList exclusions, HttpClient http,
        HttpClient hostedHttp, IRsiClient rsi, SocialListLoadResult listLoad, AppSettingsLoadResult settingsLoad)
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
        ListLoad = listLoad;
        CurrentConfig = listLoad.Config;
        SettingsLoad = settingsLoad;
        Settings = settingsLoad.Settings;
        _settingsFileUnreadable = !settingsLoad.IsValid;

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

    /// <summary>The shared RSI client. Everything that talks to RSI uses this one (it owns the 3-second rule).</summary>
    public IRsiClient Rsi { get; }

    /// <summary>The refresh interval, shared with the validator and the refresher. Changes at once when the setting is saved.</summary>
    public CacheSettings CacheSettings { get; }

    /// <summary>The settings of this PC as last saved (or as loaded). Replaced, never changed in place.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>What the settings file looked like at startup (problems to mention once).</summary>
    public AppSettingsLoadResult SettingsLoad { get; }

    /// <summary>
    /// True when settings.toml could not be read at startup and has not been replaced since: the settings in use may differ from
    /// the file even when nothing was edited, so the next Save must write the file.
    /// </summary>
    public bool SettingsFileNeedsSave => _settingsFileUnreadable;

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

        // The settings come first: they decide whether the log file is written at all.
        // Old log files are cleaned up either way (switching the log off does not leave 30 days of files behind forever).
        var settingsLoad = AppSettingsToml.Load(paths.SettingsFile);
        FileLog.DeleteOlderThan(paths.LogsFolder, TimeSpan.FromDays(30), clock.UtcNow);
        var fileLog = new FileLog(paths.LogsFolder, clock) { Enabled = settingsLoad.Settings.SessionLogEnabled };
        var log = new TeeLog(new InMemoryLog(), fileLog);

        foreach (var error in settingsLoad.Errors)
            log.Write(clock.UtcNow, "settings.toml could not be used, default settings apply: " + error);
        foreach (var warning in settingsLoad.Warnings)
            log.Write(clock.UtcNow, "settings.toml: " + warning);

        var database = CacheDatabase.OpenFile(paths.CacheFile);
        HttpClient? http = null;
        HttpClient? hostedHttp = null;
        try
        {
            var exclusions = ExclusionList.LoadOrCreate(paths.ExclusionsFile, clock, log);
            var listLoad = SocialListStore.Load(paths.SocialListFile);

            http = RsiClient.CreateHttpClient();
            var limiter = new RsiRateLimiter(clock);
            var rsi = new RsiClient(http, limiter, clock, log);
            hostedHttp = HostedConfigClient.CreateHttpClient();

            return new AppServices(paths, log, fileLog, database, exclusions, http, hostedHttp, rsi, listLoad, settingsLoad);
        }
        catch
        {
            hostedHttp?.Dispose();
            http?.Dispose();
            database.Dispose();
            throw;
        }
    }

    /// <summary>Called by the config window after a successful save: the tray's "Update database now" uses this list from then on.</summary>
    public void SetCurrentConfig(SocialConfig config)
    {
        CurrentConfig = config;
    }

    /// <summary>
    /// Saves the settings file and, only if that worked, applies them at once: the refresh interval and the log-file switch
    /// take effect without a restart. Returns the save result (nothing changes when it failed).
    /// </summary>
    public SaveResult SaveSettings(AppSettings settings)
    {
        var saved = AppSettingsToml.Save(Paths.SettingsFile, settings);
        if (saved.Success)
        {
            _settingsFileUnreadable = false; // the user's own save replaced the unreadable file (the old one stays as settings.toml.bak)
            ApplySettings(settings);
        }
        else
            Log.Write(Clock.UtcNow, "Settings not saved: " + saved.Error);
        return saved;
    }

    /// <summary>
    /// Saves a changed copy of the current settings (for example the hosted config hash or address, after an import).
    /// If settings.toml could not be read at startup the file is NOT touched: the change only lives in memory (and in the log)
    /// until the user presses Save in the Settings tab, which writes the file.
    /// </summary>
    public SaveResult UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var changed = change(Settings);
        if (!_settingsFileUnreadable)
            return SaveSettings(changed);

        ApplySettings(changed);
        Log.Write(Clock.UtcNow,
            "settings.toml could not be read at startup, so it was not overwritten; the hosted config address/version is kept in memory only until you save in the Settings tab.");
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
    /// Writes an imported config to the list file in the chosen mode. On success the app's current config (what "Update database now"
    /// uses) becomes the result and the startup load result is replaced, so the window does not treat the file as damaged any more.
    /// </summary>
    public ImportResult ApplyImport(SocialConfig imported, ImportMode mode)
    {
        var result = ConfigImporter.Import(Paths.SocialListFile, imported, mode, Clock);
        if (result.Success && result.Config is not null)
        {
            ListLoad = new SocialListLoadResult(new ConfigLoadResult { Config = result.Config }, isNew: false);
            SetCurrentConfig(result.Config);
            Log.Write(Clock.UtcNow, $"Import ({mode}): {result.Summary}");
        }
        else
        {
            Log.Write(Clock.UtcNow, $"Import ({mode}) failed: {result.Error}");
        }
        return result;
    }

    /// <summary>Refetches every entry of the saved list from RSI (slow: one request every 3 seconds). Returns a one-paragraph summary.</summary>
    public async Task<string> UpdateDatabaseNowAsync()
    {
        var config = CurrentConfig;
        if (config.Players.Count == 0 && config.Orgs.Count == 0)
            return "Nothing to update: your player and org lists are empty.";

        var token = _shutdown.Token;
        var summary = await Task.Run(() => Refresher.UpdateDatabaseNowAsync(config, token)).ConfigureAwait(true);
        return Describe(summary);
    }

    private static string Describe(RefreshSummary summary)
    {
        var parts = new List<string>
        {
            $"Updated {Count(summary.PlayersRefreshed, "player")} and {Count(summary.OrgsRefreshed, "org")}.",
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
        _shutdown.Cancel();
        _http.Dispose();
        _hostedHttp.Dispose();
        Database.Dispose();
        _shutdown.Dispose();
    }
}
