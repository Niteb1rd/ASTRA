using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.Core.Detection;
using Iris.Core.Detection.Letters;
using Iris.Core.Live;
using Iris.Core.Overlay;
using Iris.Core.Settings;
using Iris.Core.Social;
using OpenCvSharp;

namespace Iris.App.Services;

/// <summary>
/// The live detection of nameplates, in two steps: DETECT (what is on screen, and what is it called: OCR, 5 times a second) and TRACK (where is it now: template matching,
/// 15 to 120 times a second). Threads, none of them the UI thread:
/// <list type="number">
/// <item><b>Capture and tracking thread</b> ("IRIS tracking"). The ONLY owner of the screen duplication (<see cref="DxgiFrameSource"/>). While <see cref="OverlayController.Gate"/> is active
/// (Star Citizen in front and borderless, the overlay not disabled) it ticks <see cref="AppSettings.NameplateTrackingFps"/> times a second. Each tick it takes a finished
/// detection if there is one (<see cref="DetectionResultSlot"/>; a detection always wins: <see cref="PlateTemplateTracker.ApplyDetection"/>), asks the tracker which small search
/// patches it needs (<see cref="PlateTemplateTracker.Plan"/>), copies those patches, and EVERY 200 ms ALSO the whole detection scan region, out of ONE acquired frame
/// (<see cref="DxgiFrameSource.GrabRegions"/>). The scan picture is copied into the <see cref="DetectionMailbox"/> for the detection thread; the patches go to
/// <see cref="PlateTemplateTracker.Update"/> (template matching, velocity); the resulting <see cref="LiveSnapshot"/> goes into the <see cref="LiveSnapshotHub"/>. When the
/// detection thread is still busy with the last picture that slot is skipped: OCR can never hold up the tracking.</item>
/// <item><b>Detection thread</b> ("IRIS detection", below normal priority so the game wins). Waits for a picture in the mailbox, runs the <see cref="LiveDetectionEngine"/> on it
/// (detector, name reading, tracks, panel, lookups of listed-org members) and posts the <see cref="DetectionResult"/>, with the <see cref="PlateSeed"/> of every plate it saw
/// (position, template, velocity), back through the result slot. It never touches the screen.</item>
/// <item><b>UI thread: the renderer</b>, not in this class: the brackets window hooks the monitor refresh and moves the brackets of <see cref="LiveSnapshotHub.Current"/>
/// from their last tracked position along their velocity to the moment of the frame (<see cref="OverlayController"/>, <see cref="LivePredictor"/>). This class only tells the
/// overlay when something structural changed (brackets appeared or all went, a new panel): one coalesced post at a time.</item>
/// <item><b>Lookup task</b>: <see cref="OrgMemberLookupQueue"/> looks up listed-org members through the app's rate-limited RSI client, one at a time.</item>
/// </list>
/// Data flows one way each: screen -> capture thread -> (copy of the scan picture) mailbox -> detection thread -> (immutable <see cref="DetectionResult"/>) slot -> capture thread
/// -> (immutable <see cref="LiveSnapshot"/>) hub -> UI thread. The threads share only those hand-overs, immutable objects and <see cref="LiveStats"/> (which has its own lock).
/// While the gate is off nothing is captured or read at all (the loop only wakes to look at the gate); after <see cref="LiveSettings.IdleReleaseMs"/> of idleness the screen
/// duplication is released, and the tracks are forgotten the moment detection stops (<c>epoch</c>: results of an earlier run are dropped).
/// The log gets one line when detection starts and stops, and one summary line a minute while it runs: the reads (count, average and 95th percentile time, skipped slots,
/// confirmed tracks) and the tracking (ticks, average and 95th percentile time, share of matches found).
/// </summary>
public sealed class LiveDetectionService : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayController _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly IMonotonicClock _clock = StopwatchClock.Shared; // the renderer reads the same one
    private static readonly double[] EngineRetryBackoffMs = { 5000, 30000, 300000 }; // after a failed start: 5 s, 30 s, then 5 min

    private readonly LiveSettings _live = LiveSettings.Default;
    private readonly LiveSnapshotHub _hub = new();
    private readonly DetectionMailbox _mailbox = new();
    private readonly DetectionResultSlot _results = new();
    private readonly ManualResetEventSlim _wake = new(false);        // the capture thread: the gate changed
    private readonly ManualResetEventSlim _detectWake = new(false);  // the detection thread: a picture was posted, a reset was asked for
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _captureThread;
    private readonly Thread _detectionThread;
    private CancellationTokenSource? _lookupCts; // set by the detection thread; read after it ended
    private Task? _lookupTask;                   // same

    private volatile CaptureGate _gate;
    private volatile int _epoch;                 // the run of the capture loop; written by the capture thread only
    private volatile LiveStats _stats = new(0);  // replaced at every start of a run by the capture thread; both threads add to it
    private volatile int _confirmedTracks;       // set by the detection thread after each read, for the summary line
    private int _resetRequested;                 // capture -> detection thread: forget the tracks (Interlocked)
    private int _notifyPending;                  // a post to the UI thread is on its way (Interlocked)
    private bool _started;
    private bool _disposed;

    // capture thread only
    private DxgiFrameSource? _source;
    private PlateTemplateTracker? _tracker;
    private ScanGeometry? _geometry;
    private LiveSnapshot _lastNotified = LiveSnapshot.Empty;
    private double _lastPublishMs;
    private double _nextDetectMs;
    private string? _lastCaptureProblem;
    private bool _tooSmallLogged;

    /// <summary>Create it on the UI thread: the overlay is told about structural changes through that thread's dispatcher.</summary>
    public LiveDetectionService(AppServices services, OverlayController overlay)
    {
        _services = services;
        _overlay = overlay;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _gate = overlay.Gate;
        overlay.AttachLive(_hub, _live);
        overlay.GateChanged += OnGateChanged;
        _captureThread = new Thread(CaptureLoop) { Name = "IRIS tracking", IsBackground = true };
        _detectionThread = new Thread(DetectionLoop) { Name = "IRIS detection", IsBackground = true, Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Starts the threads. They do nothing until the overlay's gate opens.</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _detectionThread.Start();
        _captureThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _overlay.GateChanged -= OnGateChanged;
        _stop.Cancel();
        _wake.Set();
        _detectWake.Set();
        // the threads release the screen duplication and everything else they own before they end
        var captureEnded = !_captureThread.IsAlive || _captureThread.Join(TimeSpan.FromSeconds(2));
        var detectionEnded = !_detectionThread.IsAlive || _detectionThread.Join(TimeSpan.FromSeconds(3));
        if (!captureEnded)
            Log("Live detection: the capture thread did not end within 2 s of shutdown; its wait handles are left alone (it is a background thread and ends with the process)");
        if (!detectionEnded)
            Log("Live detection: the detection thread did not end within 3 s of shutdown; its wait handles are left alone (it is a background thread and ends with the process)");

        if (!detectionEnded)
            return; // it may still be using the lookup queue, the mailbox and the services; touching them could only turn a slow stop into errors

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
        _results.Clear();
        if (captureEnded)
        {
            _mailbox.Dispose();
            _wake.Dispose();
            _detectWake.Dispose();
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
    // Capture and tracking thread
    // ---------------------------------------------------------------------------------------

    private void CaptureLoop()
    {
        var running = false;
        var released = true;
        var idleSinceMs = 0.0;
        var nextTickMs = 0.0;

        try
        {
            _tracker = new PlateTemplateTracker(_live);
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
                        StopRun(now);
                        idleSinceMs = now;
                        released = false;
                    }
                    else if (!released && now - idleSinceMs >= _live.IdleReleaseMs)
                    {
                        _source?.Release();
                        released = true;
                    }

                    _wake.Wait(500); // nothing is captured or read here; a change of the gate wakes it at once
                    continue;
                }

                if (!running)
                {
                    StartRun(gate, now);
                    running = true;
                    released = false;
                    nextTickMs = now;
                }

                if (now < nextTickMs)
                {
                    _wake.Wait(Math.Max(1, (int)Math.Ceiling(nextTickMs - now)));
                    continue;
                }

                try
                {
                    Tick(gate, now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var text = ex.GetType().Name + ": " + ex.Message;
                    if (text != _lastCaptureProblem)
                    {
                        _lastCaptureProblem = text;
                        Log("Live detection: a tracking tick failed (" + text + "); it is tried again at the next one");
                    }
                }

                // the next tick; a tick that ran late is not followed by a burst of catch-up ticks
                var tickMs = 1000.0 / TrackingFps();
                var after = _clock.NowMs;
                nextTickMs += tickMs;
                if (nextTickMs <= after)
                    nextTickMs = after;

                if (_stats.IsDue(after, _live.SummaryEveryMs))
                    Log(_stats.TakeSummary(after, _confirmedTracks));
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
            _hub.Publish(LiveSnapshot.Empty);
            try
            {
                _tracker?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // nothing left to do at shutdown
            }

            _tracker = null;
            _source?.Dispose(); // releases the screen duplication
            _source = null;
        }
    }

    private int TrackingFps() =>
        Math.Clamp(_services.Settings.NameplateTrackingFps, AppSettings.NameplateTrackingFpsMin, AppSettings.NameplateTrackingFpsMax);

    private void StartRun(CaptureGate gate, double now)
    {
        _source ??= new DxgiFrameSource();
        _tracker!.Reset();
        _epoch = _epoch + 1;
        _stats = new LiveStats(now);
        _confirmedTracks = 0;
        _nextDetectMs = now;
        _lastCaptureProblem = null;
        _tooSmallLogged = false;
        _geometry = null;
        NativeMethods.TimeBeginPeriod(1);
        var bounds = gate.Bounds;
        Log(string.Format(
            CultureInfo.InvariantCulture,
            "Live detection started (game window {0:0}x{1:0}; names read {2:0.#} times a second, plates tracked {3} times a second, brackets drawn at monitor refresh)",
            bounds.Width, bounds.Height, _live.DetectionRateHz, TrackingFps()));
    }

    private void StopRun(double now)
    {
        NativeMethods.TimeEndPeriod(1);
        Log(_stats.TakeSummary(now, _confirmedTracks));
        _epoch = _epoch + 1; // what the detection thread is reading now belongs to the run that ended
        _tracker!.Reset();
        _mailbox.Clear();
        _results.Clear();
        Interlocked.Exchange(ref _resetRequested, 1);
        _detectWake.Set();
        Publish(LiveSnapshot.Empty); // nothing of what was on screen is shown when the game comes back
        _geometry = null;
        Log("Live detection stopped (the game is not in front and borderless, or the overlay is disabled)");
    }

    /// <summary>One tracking tick: take a finished detection, copy the patches (and now and then the scan region), match, publish.</summary>
    private void Tick(CaptureGate gate, double now)
    {
        var tracker = _tracker!;
        var stats = _stats;
        var bounds = gate.Bounds;
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        var windowX = (int)Math.Round(bounds.X);
        var windowY = (int)Math.Round(bounds.Y);

        if (_geometry is null || !_geometry.Matches(width, height))
        {
            if (!ScanGeometry.TryCreate(width, height, NameplateDetectorSettings.Default, out var created))
            {
                if (!_tooSmallLogged)
                {
                    _tooSmallLogged = true;
                    Log($"Live detection: the game window ({width}x{height}) is too small to scan");
                }

                Publish(LiveSnapshot.Empty);
                return;
            }

            _geometry = created;
            _tooSmallLogged = false;
        }

        var geometry = _geometry!;
        var tickMs = 1000.0 / TrackingFps();
        var work = 0L; // Stopwatch ticks of tracking work (without the wait for the screen)
        var mark = Stopwatch.GetTimestamp();

        // 1. a finished detection wins over everything the tracking did meanwhile
        if (_results.TryTake(out var result) && result is not null)
        {
            if (result.Epoch == _epoch && result.WindowWidth == width && result.WindowHeight == height)
                tracker.ApplyDetection(result, now); // takes over the seeds
            else
                result.Dispose(); // of a run that ended, or of another window size
        }

        // 2. what to copy: the scan region when a detection is due and the detection thread is free, and a search patch for every plate that is tracked
        var plan = tracker.Plan(now, tickMs, width, height);
        var detectDue = now >= _nextDetectMs;
        var detectSlot = detectDue && _mailbox.IsFree;
        if (detectDue && !detectSlot)
        {
            stats.AddSkippedSlots(1); // the detection thread is still reading the last picture: this slot is skipped, tracking goes on
            AdvanceDetectSlot(now, countMissed: false);
        }

        var regions = new List<Rect>(plan.Count + 1);
        if (detectSlot)
            regions.Add(geometry.ToScreen(windowX, windowY));
        foreach (var item in plan)
            regions.Add(new Rect(item.Patch.X + windowX, item.Patch.Y + windowY, item.Patch.Width, item.Patch.Height));
        work += Stopwatch.GetTimestamp() - mark;

        // 3. ONE frame, all regions
        var grab = _source!.GrabRegions(regions, detectSlot); // the scan region has its own staging slot, the patches share a stable pool
        // the time of the picture: the frame's own, taken right after it was acquired (the copies after it took time); one clock for everything
        var captured = double.IsNaN(grab.FrameMs) ? _clock.NowMs : grab.FrameMs;
        mark = Stopwatch.GetTimestamp();

        TrackTick tick;
        if (grab.Status == GrabStatus.Unavailable)
        {
            stats.AddUnavailable();
            if (grab.Message is { } message && message != _lastCaptureProblem)
            {
                _lastCaptureProblem = message; // logged when it changes, not at every tick
                Log("Live detection: no picture (" + message + ")");
            }

            tick = tracker.Update(captured, plan, null); // no picture: the plates go on along their velocity (and are hidden after 300 ms)
        }
        else
        {
            _lastCaptureProblem = null;
            var first = detectSlot ? 1 : 0;
            if (detectSlot)
            {
                var scan = grab.Images.Count > 0 ? grab.Images[0] : null;
                if (scan is not null && _mailbox.TryPost(scan, bounds, captured, _epoch))
                {
                    _detectWake.Set();
                    AdvanceDetectSlot(captured, countMissed: true);
                }
            }

            tick = tracker.Update(captured, plan, grab.Images, grab.Status == GrabStatus.NoNewFrame, first);
        }

        // 4. publish what is known now; the renderer moves it on to the time of each monitor frame
        Publish(tracker.Snapshot(captured));
        work += Stopwatch.GetTimestamp() - mark;
        if (plan.Count > 0)
            stats.AddTrackTick(work * 1000.0 / Stopwatch.Frequency, tick.Attempts, tick.Found);
    }

    /// <summary>The next detection slot: one period after the last. A tick that was so late that slots went by (<paramref name="countMissed"/>) counts them as skipped.</summary>
    private void AdvanceDetectSlot(double now, bool countMissed)
    {
        var period = _live.DetectionPeriodMs;
        _nextDetectMs += period;
        if (_nextDetectMs > now)
            return;
        var missed = (int)Math.Floor((now - _nextDetectMs) / period) + 1;
        if (countMissed)
            _stats.AddSkippedSlots(missed);
        _nextDetectMs += missed * period;
    }

    // ---------------------------------------------------------------------------------------
    // Detection thread
    // ---------------------------------------------------------------------------------------

    private void DetectionLoop()
    {
        LiveDetectionEngine? engine = null;
        var engineRetryAtMs = 0.0;
        var engineFailures = 0;
        string? lastEngineProblem = null;
        string? lastProblem = null;

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _detectWake.Reset(); // before the mailbox is looked at: a picture posted after this point wakes the next wait
                if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
                    engine?.Reset(); // the run ended: what was on screen then is not what is on screen later

                if (!_mailbox.TryTake(out var job) || job is null)
                {
                    _detectWake.Wait(250);
                    continue;
                }

                try
                {
                    if (job.Epoch != _epoch)
                        continue; // posted by a run that has ended

                    if (engine is null)
                    {
                        var at = _clock.NowMs;
                        if (at < engineRetryAtMs)
                            continue; // still waiting to try again after a failed start
                        try
                        {
                            engine = StartEngine();
                            engineFailures = 0;
                            lastEngineProblem = null;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
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

                            engineRetryAtMs = _clock.NowMs + retryMs;
                            continue;
                        }
                    }

                    var started = _clock.NowMs;
                    DetectionResult? result = null;
                    try
                    {
                        result = engine.Process(job.Image, job.Window, _services.CurrentConfig, job.CaptureMs, out var processProblem, buildSeeds: true, epoch: job.Epoch);
                        if (result is null && processProblem is not null && processProblem != lastProblem)
                        {
                            lastProblem = processProblem;
                            Log("Live detection: " + processProblem);
                        }
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

                    if (result is not null)
                    {
                        lastProblem = null;
                        _stats.AddRead(_clock.NowMs - started);
                        _confirmedTracks = result.Snapshot.Brackets.Count;
                        _results.Post(result); // the capture loop takes it at its next tick
                    }
                    else
                    {
                        _stats.AddUnavailable();
                    }
                }
                finally
                {
                    _mailbox.Complete(); // the picture is not needed any more (the templates are copies)
                }
            }
        }
        catch (Exception ex)
        {
            Log("Live detection stopped because of an unexpected error: " + ex);
        }
        finally
        {
            try
            {
                _lookupCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            engine?.Dispose();
        }
    }

    private LiveDetectionEngine StartEngine()
    {
        LiveDetectionEngine? created = null;
        try
        {
            created = CreateEngine(out var queue);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_services.ShutdownToken, _stop.Token);
            var token = cts.Token;
            _lookupCts = cts;
            _lookupTask = Task.Run(() => queue.RunAsync(token), token);
            return created; // only a completely started engine is kept
        }
        catch
        {
            created?.Dispose();
            throw;
        }
    }

    private LiveDetectionEngine CreateEngine(out OrgMemberLookupQueue queue)
    {
        var reader = new NameplateReader(LetterModel.LoadEmbedded());
        queue = OrgMemberLookupQueue.For(_services.Database, _services.Refresher, _services.Clock, _services.Log);
        var duplicates = new DuplicateNameTracker(_services.Database, _services.Exclusions, _services.Clock);
        return new LiveDetectionEngine(
            reader, _services.Exclusions, LookupCachedOrgs, duplicates, queue, _live, detectorSettings: null, trackerSettings: null,
            log: _services.Log, clock: _services.Clock);
    }

    /// <summary>The orgs of a handle from the cache, null when the player was never looked up. A cache problem is handled by the engine (the player shows as "not looked up").</summary>
    private IReadOnlyList<OrgMembership>? LookupCachedOrgs(string handle) => _services.Database.GetPlayer(handle)?.Orgs;

    // ---------------------------------------------------------------------------------------
    // Handing snapshots to the renderer
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Capture thread: the renderer's snapshot. The renderer (UI thread, every monitor frame) reads the hub by itself; the overlay is only TOLD when something structural
    /// changed: brackets appeared or all of them went (a window must be shown or hidden), or the panel's entries changed (the panel window redraws). At most one post is on its way.
    /// </summary>
    private void Publish(LiveSnapshot snapshot)
    {
        _hub.Publish(snapshot);
        // after a stall (no publish for longer than StaleHideMs) the renderer has hidden the brackets by itself and its hook is gone: it must be told that there is something again
        var at = _clock.NowMs;
        var stalled = at - _lastPublishMs > _live.StaleHideMs;
        _lastPublishMs = at;
        var last = _lastNotified;
        if (!stalled && (snapshot.Brackets.Count > 0) == (last.Brackets.Count > 0) && ReferenceEquals(snapshot.Panel, last.Panel))
            return;
        _lastNotified = snapshot;
        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DeliverChange));
    }

    // UI thread
    private void DeliverChange()
    {
        Interlocked.Exchange(ref _notifyPending, 0);
        if (!_disposed)
            _overlay.LiveChanged();
    }

    private void Log(string text) => _services.Log.Write(_services.Clock.UtcNow, text);
}
