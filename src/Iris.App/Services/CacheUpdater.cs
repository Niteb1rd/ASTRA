using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.App.Services;

/// <summary>
/// The two ways IRIS fills its RSI cache from the lists, both through the one shared rate limiter:
///
/// - <see cref="RunFullRefreshAsync"/>: the "Refresh Cache (long time)" button. Refetches every player and org on the saved lists, whatever
///   their age. One at a time; <see cref="RefreshingChanged"/> lets the button grey itself out.
/// - <see cref="StartFillMissing"/>: started automatically after a new config was applied (imported, or loaded from an edited file).
///   Fetches only the players and orgs that have no cache entry or an entry older than the refresh interval, in the background.
///   A newer config replaces a fill that is still running. When it ends and anything was asked of RSI, <see cref="FillMissingFinished"/>
///   carries a short report.
///
/// Both stop when IRIS quits (<see cref="AppServices.ShutdownToken"/>). Everything here runs on the UI thread (events are raised there).
/// </summary>
public sealed class CacheUpdater
{
    private readonly AppServices _services;

    // The running fill-missing job, if any. Only touched on the UI thread.
    private CancellationTokenSource? _fillCts;

    // A config was applied while the full refresh was running: fill again when it is done (the full refresh uses the list it started with).
    private bool _fillPending;

    public CacheUpdater(AppServices services)
    {
        _services = services;
        services.ListApplied += (_, _) => StartFillMissing();
    }

    /// <summary>True from the start of a full refresh until it ends.</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>True while a fill-missing run is going.</summary>
    public bool IsFilling => _fillCts is not null;

    /// <summary>Raised when <see cref="IsRefreshing"/> changes.</summary>
    public event EventHandler? RefreshingChanged;

    /// <summary>Raised when a fill-missing run ended after asking RSI for something. The argument is the report.</summary>
    public event EventHandler<string>? FillMissingFinished;

    /// <summary>
    /// Runs the full refresh and returns one short paragraph for the status bar. Returns null when a refresh is already running or
    /// IRIS is quitting. Never throws.
    /// </summary>
    public async Task<string?> RunFullRefreshAsync()
    {
        if (IsRefreshing || _services.IsShuttingDown)
            return null;

        _fillCts?.Cancel(); // the full refresh covers everything the fill would do
        SetRefreshing(true);
        try
        {
            var message = await _services.RefreshAllAsync();
            _services.Log.Write(_services.Clock.UtcNow, "Refresh Cache: " + message);
            return message;
        }
        catch (OperationCanceledException)
        {
            return null; // IRIS is quitting
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Refresh Cache failed: " + ex.Message);
            return "The cache refresh failed: " + ex.Message;
        }
        finally
        {
            SetRefreshing(false);
            if (_fillPending && !_services.IsShuttingDown)
            {
                _fillPending = false;
                StartFillMissing();
            }
        }
    }

    /// <summary>Starts a background fill-missing run for the current saved list (replacing one that is still running).</summary>
    public void StartFillMissing()
    {
        if (_services.IsShuttingDown)
            return;
        if (IsRefreshing)
        {
            _fillPending = true;
            return;
        }

        _fillCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken);
        _fillCts = cts;
        _ = RunFillMissingAsync(cts);
    }

    private async Task RunFillMissingAsync(CancellationTokenSource cts)
    {
        try
        {
            var config = _services.CurrentConfig;
            if (config.Players.Count == 0 && config.Orgs.Count == 0)
                return;

            var summary = await _services.FillMissingAsync(config, cts.Token).ConfigureAwait(true);
            if (summary.Attempted > 0 && !cts.IsCancellationRequested)
            {
                var message = AppServices.Describe("Cache filled:", summary);
                _services.Log.Write(_services.Clock.UtcNow, "Fill missing: " + message);
                FillMissingFinished?.Invoke(this, message);
            }
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer run, replaced by the full refresh, or IRIS is quitting
        }
        catch (Exception ex)
        {
            _services.Log.Write(_services.Clock.UtcNow, "Fill missing failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_fillCts, cts))
                _fillCts = null;
            cts.Dispose();
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        IsRefreshing = refreshing;
        RefreshingChanged?.Invoke(this, EventArgs.Empty);
    }
}
