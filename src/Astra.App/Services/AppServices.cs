using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core;
using Astra.Core.Cache;
using Astra.Core.Config;
using Astra.Core.Detection;
using Astra.Core.Rsi;
using Astra.Core.Storage;

namespace Astra.App.Services;

/// <summary>
/// Everything the app shares: the data folder, the log, the cache database, the exclusion list, the ONE RSI client
/// (with its one rate limiter) and the list the user saved. Windows and the tray get what they need from here.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _shutdown = new();

    private AppServices(
        DataPaths paths, ILog log, CacheDatabase database, ExclusionList exclusions, HttpClient http,
        IRsiClient rsi, SocialListLoadResult listLoad)
    {
        Paths = paths;
        Log = log;
        Database = database;
        Exclusions = exclusions;
        _http = http;
        Rsi = rsi;
        ListLoad = listLoad;
        CurrentConfig = listLoad.Config;

        CacheSettings = new CacheSettings();
        Validator = new RowValidator(database, rsi, Clock, CacheSettings, log);
        Refresher = new CacheRefresher(database, rsi, Clock, CacheSettings, log);
    }

    /// <summary>
    /// The one place that decides where log lines go. Next increment this becomes the session log file:
    /// change this single line.
    /// </summary>
    public static ILog CreateLog() => new InMemoryLog();

    public IClock Clock => SystemClock.Instance;

    public DataPaths Paths { get; }

    public ILog Log { get; }

    public CacheDatabase Database { get; }

    public ExclusionList Exclusions { get; }

    /// <summary>The shared RSI client. Everything that talks to RSI uses this one (it owns the 3-second rule).</summary>
    public IRsiClient Rsi { get; }

    public CacheSettings CacheSettings { get; }

    public RowValidator Validator { get; }

    public CacheRefresher Refresher { get; }

    /// <summary>What the list file looked like at startup (errors and warnings to show once).</summary>
    public SocialListLoadResult ListLoad { get; }

    /// <summary>The list as last saved (or as loaded). Replaced, never changed in place, when the user saves.</summary>
    public SocialConfig CurrentConfig { get; private set; }

    /// <summary>Remembered for the overlay increment; nothing reads it yet.</summary>
    public bool IsOverlayPaused { get; set; }

    /// <summary>Opens everything under the Data folder next to the program. Throws if the cache file cannot be opened.</summary>
    public static AppServices Create(string exeDirectory)
    {
        var paths = DataPaths.FromBaseDirectory(exeDirectory);
        paths.EnsureCreated();

        var log = CreateLog();
        var clock = SystemClock.Instance;

        var database = CacheDatabase.OpenFile(paths.CacheFile);
        HttpClient? http = null;
        try
        {
            var exclusions = ExclusionList.LoadOrCreate(paths.ExclusionsFile, clock, log);
            var listLoad = SocialListStore.Load(paths.SocialListFile);

            http = RsiClient.CreateHttpClient();
            var limiter = new RsiRateLimiter(clock);
            var rsi = new RsiClient(http, limiter, clock, log);

            return new AppServices(paths, log, database, exclusions, http, rsi, listLoad);
        }
        catch
        {
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

    public void Dispose()
    {
        _shutdown.Cancel();
        _http.Dispose();
        Database.Dispose();
        _shutdown.Dispose();
    }
}
