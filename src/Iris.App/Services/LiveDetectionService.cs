using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Settings;
using Iris.Core.Social;

namespace Iris.App.Services;

/// <summary>
/// The live detection of nameplates: runs the screen reads and the bracket movement on background threads and hands the overlay a new
/// <see cref="OverlayModel"/> only when something changed. Three threads of work, none of them the UI thread:
/// <list type="number">
/// <item><b>Detection thread</b> ("IRIS detection", below normal priority so the game wins). While <see cref="OverlayController.Gate"/> is active (Star Citizen in front and borderless, the
/// overlay not disabled) it does 5 reads a second (<see cref="LiveSettings.DetectionRateHz"/>): <see cref="DxgiFrameSource"/> captures the scan region, the
/// <see cref="LiveDetectionEngine"/> runs detector and tracker and builds a <see cref="LiveSnapshot"/>. A read that takes longer than its period is never followed by a catch-up:
/// the slots it overran are skipped. While the gate is off the thread captures and reads nothing at all (it only wakes to look at the gate); after
/// <see cref="LiveSettings.IdleReleaseMs"/> of idleness the screen duplication is released, and detection state is cleared the moment it stops.</item>
/// <item><b>Tracking thread</b> ("IRIS tracking"). While detection runs it wakes <see cref="AppSettings.NameplateTrackingFps"/> times a second (15 to 120), asks
/// <see cref="LivePredictor"/> for the model at that moment (the brackets moved along their plates' speeds) and, only if a bracket moved by a whole pixel, posts it to the UI thread.
/// That is arithmetic only: no capture and no OCR.</item>
/// <item><b>Lookup task</b>: <see cref="OrgMemberLookupQueue"/> looks up listed-org members through the app's rate-limited RSI client, one at a time.</item>
/// </list>
/// The threads share only immutable objects (<see cref="LiveSnapshot"/>, <see cref="OverlayModel"/>, <see cref="CaptureGate"/>, <see cref="AppSettings"/>, <see cref="SocialConfig"/>)
/// and one lock around "snapshot + latest model". Posts to the UI thread are coalesced: while one is waiting, newer models only replace what it will deliver, so a slow UI never builds a backlog.
/// The log gets one line when detection starts and stops, and one summary line a minute while it runs (reads, average and 95th percentile time, skipped slots, confirmed tracks).
/// </summary>
public sealed class LiveDetectionService : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayController _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly IMonotonicClock _clock = new StopwatchClock();
    private static readonly double[] EngineRetryBackoffMs = { 5000, 30000, 300000 }; // after a failed start: 5 s, 30 s, then 5 min

    private readonly LiveSettings _live = LiveSettings.Default;

    // The names the duplicate rule excluded: lives as long as IRIS runs, so it survives every engine that is made and every Reset (alt-tab).
    private readonly SessionExclusions _sessionExclusions = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _tickWake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _publishGate = new();
    private readonly Thread _detectionThread;
    private readonly Thread _trackingThread;
    private CancellationTokenSource? _lookupCts; // set by the detection thread; read after it ended
    private Task? _lookupTask;                   // same

    private volatile CaptureGate _gate;
    private volatile bool _running;
    private LiveSnapshot _snapshot = LiveSnapshot.Empty; // guarded by _publishGate
    private OverlayModel _model = OverlayModel.Empty;    // guarded by _publishGate
    private bool _postPending;                           // guarded by _publishGate
    private bool _started;
    private bool _disposed;

    /// <summary>Create it on the UI thread: the overlay is told about new models through that thread's dispatcher.</summary>
    public LiveDetectionService(AppServices services, OverlayController overlay)
    {
        _services = services;
        _overlay = overlay;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _gate = overlay.Gate;
        overlay.GateChanged += OnGateChanged;
        _detectionThread = new Thread(DetectionLoop) { Name = "IRIS detection", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _trackingThread = new Thread(TrackingLoop) { Name = "IRIS tracking", IsBackground = true };
    }

    /// <summary>Starts the threads. They do nothing until the overlay's gate opens.</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _detectionThread.Start();
        _trackingThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _overlay.GateChanged -= OnGateChanged;
        _stop.Cancel();
        _wake.Set();
        _tickWake.Set();
        // the threads release the screen duplication and everything else they own before they end
        var detectionEnded = !_detectionThread.IsAlive || _detectionThread.Join(TimeSpan.FromSeconds(3));
        var trackingEnded = !_trackingThread.IsAlive || _trackingThread.Join(TimeSpan.FromSeconds(1));
        if (!detectionEnded)
            Log("Live detection: the detection thread did not end within 3 s of shutdown; its wait handles are left alone (it is a background thread and ends with the process)");
        if (!trackingEnded)
            Log("Live detection: the tracking thread did not end within 1 s of shutdown; its wait handles are left alone");

        if (!detectionEnded)
            return; // it may still be using the lookup queue, the wait handles and the services; touching them could only turn a slow stop into errors

        // the lookup task was cancelled by the thread; give it a moment to finish its current RSI request before the services go away
        try
        {
            _lookupTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // cancelled or failed: nothing left to do
        }

        _lookupCts?.Dispose();
        if (trackingEnded)
        {
            _wake.Dispose();
            _tickWake.Dispose();
            _stop.Dispose();
        }
    }

    // UI thread
    private void OnGateChanged(object? sender, EventArgs e)
    {
        _gate = _overlay.Gate;
        _wake.Set();
    }

    // ---------------------------------------------------------------------------------------
    // Detection thread
    // ---------------------------------------------------------------------------------------

    private void DetectionLoop()
    {
        DxgiFrameSource? source = null;
        LiveDetectionEngine? engine = null;
        var engineRetryAtMs = 0.0;
        var engineFailures = 0;
        string? lastEngineProblem = null;
        var running = false;
        var released = true;
        var idleSinceMs = 0.0;
        var nextMs = 0.0;
        var reads = 0L;
        var stats = new LiveStats(0);
        string? lastProblem = null;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _wake.Reset(); // before the gate is read: a change after this point wakes the next wait
                var gate = _gate;
                var now = _clock.NowMs;

                if (!gate.Active)
                {
                    if (running)
                    {
                        running = false;
                        _running = false;
                        NativeMethods.TimeEndPeriod(1);
                        Log(stats.TakeSummary(now, engine?.Snapshot.Brackets.Count ?? 0));
                        engine?.Reset();
                        Publish(LiveSnapshot.Empty); // nothing of what was on screen is shown when the game comes back
                        Log($"Live detection stopped (the game is not in front and borderless, or the overlay is disabled); {reads} reads this time");
                        idleSinceMs = now;
                        released = false;
                    }
                    else if (!released && now - idleSinceMs >= _live.IdleReleaseMs)
                    {
                        source?.Release();
                        released = true;
                    }

                    _wake.Wait(500); // nothing is captured or read here; a change of the gate wakes it at once
                    continue;
                }

                if (!running)
                {
                    source ??= new DxgiFrameSource();
                    if (engine is null)
                    {
                        if (now < engineRetryAtMs)
                        {
                            _wake.Wait(Math.Max(1, (int)Math.Ceiling(engineRetryAtMs - now)));
                            continue;
                        }

                        LiveDetectionEngine? created = null;
                        try
                        {
                            created = CreateEngine(out var queue);
                            var cts = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken, _stop.Token);
                            var token = cts.Token;
                            _lookupCts = cts;
                            _lookupTask = Task.Run(() => queue.RunAsync(token), token);
                            engine = created; // only a completely started engine is kept
                            engineFailures = 0;
                            lastEngineProblem = null;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            created?.Dispose();
                            // for example the letter model or the cache could not be opened: the loop stays alive and tries again
                            // logged only when the error changes; the pause before the next try grows: 5 s, 30 s, then 5 min
                            var retryMs = EngineRetryBackoffMs[Math.Min(engineFailures, EngineRetryBackoffMs.Length - 1)];
                            engineFailures++;
                            var problem = ex.GetType().Name + ": " + ex.Message;
                            if (problem != lastEngineProblem)
                            {
                                lastEngineProblem = problem;
                                Log("Live detection could not start (" + problem + "); trying again in " + retryMs / 1000 + " s, then less often while it keeps failing");
                            }

                            engine = null;
                            engineRetryAtMs = _clock.NowMs + retryMs;
                            continue;
                        }
                    }

                    running = true;
                    released = false;
                    reads = 0;
                    lastProblem = null;
                    stats = new LiveStats(now);
                    nextMs = now;
                    NativeMethods.TimeBeginPeriod(1);
                    _running = true;
                    _tickWake.Set();
                    var bounds = gate.Bounds;
                    Log(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Live detection started (game window {0:0}x{1:0}, {2:0.#} reads a second, brackets move {3} times a second)",
                        bounds.Width, bounds.Height, _live.DetectionRateHz, _services.Settings.NameplateTrackingFps));
                }

                if (now < nextMs)
                {
                    _wake.Wait(Math.Max(1, (int)Math.Ceiling(nextMs - now)));
                    continue;
                }

                // ---- one read ----
                var started = _clock.NowMs;
                StepResult? result = null;
                try
                {
                    result = engine!.Step(source!, gate.Bounds, _services.CurrentConfig, started);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var text = ex.GetType().Name + ": " + ex.Message;
                    if (text != lastProblem)
                    {
                        lastProblem = text;
                        Log("Live detection: a read failed (" + text + "); it is tried again at the next one");
                    }
                }

                var finished = _clock.NowMs;
                if (result is { Status: StepStatus.Read })
                {
                    stats.AddRead(finished - started);
                    reads++;
                    lastProblem = null;
                    Publish(result.Snapshot);
                }
                else
                {
                    stats.AddUnavailable();
                    if (result?.Message is { } message && message != lastProblem)
                    {
                        lastProblem = message; // logged when it changes, not at every read
                        Log("Live detection: no picture (" + message + ")");
                    }
                }

                // The next slot. A read that ran past its period does not get a catch-up: the slots it overran are skipped, reads never queue up.
                var period = _live.DetectionPeriodMs;
                nextMs += period;
                if (nextMs <= finished)
                {
                    var missed = (int)Math.Floor((finished - nextMs) / period) + 1;
                    stats.AddSkippedSlots(missed);
                    nextMs += missed * period;
                }

                if (stats.IsDue(finished, _live.SummaryEveryMs))
                    Log(stats.TakeSummary(finished, engine!.Snapshot.Brackets.Count));
            }
        }
        catch (Exception ex)
        {
            Log("Live detection stopped because of an unexpected error: " + ex);
        }
        finally
        {
            if (running)
                NativeMethods.TimeEndPeriod(1);
            _running = false;
            try
            {
                _lookupCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            engine?.Dispose();
            source?.Dispose(); // releases the screen duplication
        }
    }

    private LiveDetectionEngine CreateEngine(out OrgMemberLookupQueue queue)
    {
        var reader = new NameplateReader(LetterModel.LoadEmbedded());
        queue = OrgMemberLookupQueue.For(_services.Database, _services.Refresher, _services.Clock, _services.Log);
        return new LiveDetectionEngine(
            reader, _services.Exclusions, LookupCachedOrgs, _sessionExclusions, queue, _live, detectorSettings: null, trackerSettings: null,
            log: _services.Log, clock: _services.Clock, isOrgMember: (sid, handle) => _services.Database.IsOrgMember(sid, handle));
    }

    /// <summary>The orgs of a handle from the cache, null when the player was never looked up. A cache problem is handled by the engine (the player shows as "not looked up").</summary>
    private IReadOnlyList<OrgMembership>? LookupCachedOrgs(string handle) => _services.Database.GetPlayer(handle)?.Orgs;

    // ---------------------------------------------------------------------------------------
    // Tracking thread
    // ---------------------------------------------------------------------------------------

    private void TrackingLoop()
    {
        try
        {
            var next = 0.0;
            while (!_stop.IsCancellationRequested)
            {
                if (!_running)
                {
                    _tickWake.Reset();
                    if (!_running)
                        _tickWake.Wait(1000);
                    next = 0.0;
                    continue;
                }

                if (next == 0.0)
                    next = _clock.NowMs;
                PushModel();

                var fps = Math.Clamp(_services.Settings.NameplateTrackingFps, AppSettings.NameplateTrackingFpsMin, AppSettings.NameplateTrackingFpsMax);
                next += 1000.0 / fps;
                var now = _clock.NowMs;
                if (next <= now)
                    next = now; // behind (a busy machine): no catch-up burst
                else
                    Thread.Sleep(Math.Max(1, (int)Math.Round(next - now)));
            }
        }
        catch (Exception ex)
        {
            Log("Bracket tracking stopped because of an unexpected error: " + ex);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Handing models to the UI thread
    // ---------------------------------------------------------------------------------------

    /// <summary>A read finished: the new snapshot becomes the base of the predictions, and the overlay hears about it at once (a corrected name changes color now, not at the next tick).</summary>
    private void Publish(LiveSnapshot snapshot)
    {
        lock (_publishGate)
            _snapshot = snapshot;
        PushModel();
    }

    /// <summary>
    /// Computes the model for this moment and posts it to the UI thread, unless it is the very same model as the last one (no bracket moved by a pixel): then no
    /// redraw is asked for at all. At most one post is on its way; a model computed meanwhile replaces what that post will deliver.
    /// </summary>
    private void PushModel()
    {
        lock (_publishGate)
        {
            var model = LivePredictor.ModelAt(_snapshot, _clock.NowMs, _live, _model);
            if (ReferenceEquals(model, _model))
                return;
            _model = model;
            if (_postPending)
                return;
            _postPending = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Deliver));
    }

    // UI thread
    private void Deliver()
    {
        OverlayModel model;
        lock (_publishGate)
        {
            model = _model;
            _postPending = false;
        }

        if (!_disposed)
            _overlay.SetLiveModel(model);
    }

    private void Log(string text) => _services.Log.Write(_services.Clock.UtcNow, text);
}
