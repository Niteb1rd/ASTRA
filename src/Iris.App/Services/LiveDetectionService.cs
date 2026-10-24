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
/// on every new frame of the game, at most <see cref="AppSettings.NameplateTrackingFps"/> times a second or with no cap for Max). Threads, none of them the UI thread:
/// <list type="number">
/// <item><b>Capture and tracking thread</b> ("IRIS tracking"). The ONLY owner of the screen duplication (<see cref="DxgiFrameSource"/>). While <see cref="OverlayController.Gate"/> is active
/// (Star Citizen in front and borderless, the overlay not disabled) it is EVENT DRIVEN: it waits in <c>AcquireNextFrame</c> for the screen's next new picture and stamps it with the time
/// it was presented (<see cref="FrameClock"/>); a frame that comes sooner than one tracking period after the last tracked one (<see cref="TrackingSchedule"/>) is released unused. For a
/// frame that is tracked it takes a finished
/// detection if there is one (<see cref="DetectionResultSlot"/>; a detection always wins: <see cref="PlateTemplateTracker.ApplyDetection"/>), asks the tracker which small search
/// patches it needs (<see cref="PlateTemplateTracker.Plan"/>), copies those patches, and at most every 200 ms ALSO the whole detection scan region, out of that ONE acquired frame
/// (<see cref="DxgiFrameSource.CopyHeldFrame"/>). A still screen holds the brackets where they are. The scan picture is copied into the <see cref="DetectionMailbox"/> for the detection thread; the patches go to
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

    /// <summary>On a still screen the plates are held, and a due detection reads the previous picture again, every this many milliseconds (the wait for the screen is the pause between).</summary>
    private const double HoldStepMs = 50.0;

    private const int UnavailablePauseMs = 50;   // nothing can be captured: look again in 50 ms (never a tight loop)
    private const int ErrorPauseMs = 50;         // a step threw: do not loop tightly
    private const int TooSmallPauseMs = 100;     // the game window is too small to scan

    private readonly LiveSettings _live = LiveSettings.Default;

    // The names the duplicate rule excluded: lives as long as IRIS runs, so it survives every engine that is made and every Reset (alt-tab).
    private readonly SessionExclusions _sessionExclusions = new();
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
    private double _lastFrameMs;       // the time of the newest frame (or hold step) so far: frame times never go back
    private readonly TrackingSchedule _schedule = new();
    private double _lastTrackedMs = double.NaN; // the time of the last tracked frame (NaN: none yet in this run)
    private double _lastStepMs;        // the clock when the last frame was tracked or the last hold step ran
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
                }

                // Event driven: the wait for the screen's next new picture is INSIDE Step (AcquireNextFrame), so this loop runs once per new frame of the game (or once per
                // timeout on a still screen), not on a timer. Step decides whether the frame is tracked (the cap) and does the work.
                var pause = 0;
                try
                {
                    pause = Step(gate);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _source?.ReleaseHeldFrame(); // never keep a frame across a failed step
                    var text = ex.GetType().Name + ": " + ex.Message;
                    if (text != _lastCaptureProblem)
                    {
                        _lastCaptureProblem = text;
                        Log("Live detection: a tracking step failed (" + text + "); it is tried again at the next one");
                    }

                    pause = ErrorPauseMs; // a failing step must not become a tight loop
                }

                if (pause > 0)
                    _wake.Wait(pause); // a change of the gate wakes it at once

                var after = _clock.NowMs;
                if (_stats.IsDue(after, _live.SummaryEveryMs))
                {
                    Log(_stats.TakeSummary(after, _confirmedTracks, TrackingCapFps()));
                    LogRenderSummary(); // the brackets window's "Overlay render" line goes right after it, also once a minute
                }
            }
        }
        catch (Exception ex)
        {
            Log("Live detection stopped because of an unexpected error: " + ex);
        }
        finally
        {
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

    /// <summary>The tracking cap in frames a second (<see cref="AppSettings.TrackingCapFps"/>), 0 for Max: every new frame is tracked. Read at every frame: a saved change applies at once.</summary>
    private int TrackingCapFps() => _services.Settings.TrackingCapFps;

    private void StartRun(CaptureGate gate, double now)
    {
        _source ??= new DxgiFrameSource();
        _tracker!.Reset();
        _epoch = _epoch + 1;
        _stats = new LiveStats(now);
        _confirmedTracks = 0;
        _nextDetectMs = now;
        _lastFrameMs = now;
        _lastTrackedMs = double.NaN;
        _schedule.Reset();
        _lastStepMs = now;
        _lastCaptureProblem = null;
        _tooSmallLogged = false;
        _geometry = null;
        var bounds = gate.Bounds;
        var cap = TrackingCapFps();
        Log(string.Format(
            CultureInfo.InvariantCulture,
            "Live detection started (game window {0:0}x{1:0}; names read {2:0.#} times a second, plates tracked on every new frame of the game{3}, brackets drawn at monitor refresh)",
            bounds.Width, bounds.Height, _live.DetectionRateHz, cap > 0 ? $", at most {cap} times a second" : " with no cap (Max)"));
    }

    private void StopRun(double now)
    {
        _source?.ReleaseHeldFrame();
        Log(_stats.TakeSummary(now, _confirmedTracks, TrackingCapFps()));
        LogRenderSummary();
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

    /// <summary>Why a <see cref="Process"/> runs: a new frame of the game (the held frame is copied), a still screen (the previous pictures are read again), or nothing could be captured.</summary>
    private enum StepKind
    {
        NewFrame,
        Unchanged,
        NoPicture,
    }

    /// <summary>
    /// One step of the event-driven loop: wait for the screen's next NEW picture (<see cref="DxgiFrameSource.AcquireNextFrame"/>, up to
    /// <see cref="DxgiFrameSource.AcquireTimeoutMs"/>), and for a new frame decide whether to track it (<see cref="TrackingSchedule"/>: a running schedule that meets the cap on average, with 1 ms of slack; the
    /// last tracked frame; Max tracks every one); a frame that is not tracked is released at once and the loop waits again. The time of a frame is its own present time
    /// (<see cref="FrameClock"/>), never earlier than the frame before. A still screen (a timeout, or a wake for the mouse pointer only) is a "hold" step every
    /// <see cref="HoldStepMs"/>: the brackets hold where they are, the plates that were found stay seen, and the previous scan picture is read again when a detection is due.
    /// Returns how long to pause before the next step in milliseconds (0: none, the wait for the screen is the pause).
    /// </summary>
    private int Step(CaptureGate gate)
    {
        var bounds = gate.Bounds;
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        if (!EnsureGeometry(width, height))
        {
            Publish(LiveSnapshot.Empty);
            return TooSmallPauseMs;
        }

        var source = _source!;
        var stats = _stats;
        var arrival = source.AcquireNextFrame(new Rect((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), width, height));
        switch (arrival.Status)
        {
            case FrameWait.NewFrame:
            {
                var frameMs = Math.Max(arrival.FrameMs, _lastFrameMs);
                _lastFrameMs = frameMs;
                stats.AddGameFrame(frameMs);
                if (!_schedule.Offer(frameMs, TrackingCapFps()))
                {
                    source.ReleaseHeldFrame(); // not this one: wait for the next
                    return 0;
                }

                var sinceLast = double.IsNaN(_lastTrackedMs) ? 1000.0 / 60.0 : frameMs - _lastTrackedMs;
                _lastTrackedMs = frameMs;
                _lastStepMs = _clock.NowMs;
                Process(gate, frameMs, Math.Max(1.0, sinceLast), StepKind.NewFrame, null);
                stats.AddTrackedFrame();
                return 0;
            }

            case FrameWait.Unchanged:
            {
                var now = _clock.NowMs;
                if (now - _lastStepMs < HoldStepMs - 5.0)
                    return 0; // woken early (the mouse pointer): the next wait is the pause
                _lastStepMs = now;
                _lastFrameMs = Math.Max(_lastFrameMs, now); // the next frame is never stamped earlier than this step
                Process(gate, now, HoldStepMs, StepKind.Unchanged, null);
                return 0;
            }

            default:
            {
                var now = _clock.NowMs;
                _lastStepMs = now;
                _lastFrameMs = Math.Max(_lastFrameMs, now);
                Process(gate, now, HoldStepMs, StepKind.NoPicture, arrival.Message);
                return UnavailablePauseMs;
            }
        }
    }

    /// <summary>The scan geometry for a window of this size (made again when the size changes). False, with one log line, when the window is too small to scan.</summary>
    private bool EnsureGeometry(int width, int height)
    {
        if (_geometry is not null && _geometry.Matches(width, height))
            return true;
        if (!ScanGeometry.TryCreate(width, height, NameplateDetectorSettings.Default, out var created))
        {
            if (!_tooSmallLogged)
            {
                _tooSmallLogged = true;
                Log($"Live detection: the game window ({width}x{height}) is too small to scan");
            }

            return false;
        }

        _geometry = created;
        _tooSmallLogged = false;
        return true;
    }

    /// <summary>
    /// The work for one step at <paramref name="timeMs"/> (the frame's own time for a new frame): take a finished detection, copy the patches (and now and then the scan region)
    /// out of the held frame, match, publish.
    /// </summary>
    private void Process(CaptureGate gate, double timeMs, double tickMs, StepKind kind, string? problem)
    {
        var tracker = _tracker!;
        var stats = _stats;
        var bounds = gate.Bounds;
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        var windowX = (int)Math.Round(bounds.X);
        var windowY = (int)Math.Round(bounds.Y);
        var geometry = _geometry!;
        var work = 0L; // Stopwatch ticks of tracking work (without the wait for the screen)
        var mark = Stopwatch.GetTimestamp();

        // 1. a finished detection wins over everything the tracking did meanwhile
        if (_results.TryTake(out var result) && result is not null)
        {
            if (result.Epoch == _epoch && result.WindowWidth == width && result.WindowHeight == height)
                tracker.ApplyDetection(result, _clock.NowMs); // takes over the seeds
            else
                result.Dispose(); // of a run that ended, or of another window size
        }

        // 2. what to copy: the scan region when a detection is due and the detection thread is free (a new frame, or the previous picture on a still screen),
        // and a search patch for every plate that is tracked
        var plan = tracker.Plan(timeMs, tickMs, width, height);
        var detectDue = kind != StepKind.NoPicture && timeMs >= _nextDetectMs;
        var detectSlot = detectDue && _mailbox.IsFree;
        if (detectDue && !detectSlot)
        {
            stats.AddSkippedSlots(1); // the detection thread is still reading the last picture: this slot is skipped, tracking goes on
            AdvanceDetectSlot(timeMs, countMissed: false);
        }

        var regions = new List<Rect>(plan.Count + 1);
        if (detectSlot)
            regions.Add(geometry.ToScreen(windowX, windowY));
        foreach (var item in plan)
            regions.Add(new Rect(item.Patch.X + windowX, item.Patch.Y + windowY, item.Patch.Width, item.Patch.Height));
        work += Stopwatch.GetTimestamp() - mark;

        // 3. ONE frame, all regions (the scan region has its own staging slot, the patches share a stable pool); the frame is released by the copy
        GrabRegionsResult grab = kind switch
        {
            StepKind.NewFrame => _source!.CopyHeldFrame(regions, detectSlot),
            StepKind.Unchanged => _source!.UnchangedRegions(regions, detectSlot),
            _ => GrabRegionsResult.Unavailable(problem ?? "screen capture is not available"),
        };
        var captured = timeMs; // the time of the picture: the frame's own present time; one clock for everything
        mark = Stopwatch.GetTimestamp();

        TrackTick tick;
        if (grab.Status == GrabStatus.Unavailable)
        {
            stats.AddUnavailable();
            if (grab.Message is { } message && message != _lastCaptureProblem)
            {
                _lastCaptureProblem = message; // logged when it changes, not at every step
                Log("Live detection: no picture (" + message + ")");
            }

            tick = tracker.Update(captured, plan, null); // no picture: the plates go on along their velocity (and are hidden after 300 ms)
        }
        else
        {
            _lastCaptureProblem = null; // a picture again: a new problem is logged afresh
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
        if (plan.Count > 0 && kind == StepKind.NewFrame)
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
        return new LiveDetectionEngine(
            reader, _services.Exclusions, LookupCachedOrgs, _sessionExclusions, queue, _live, detectorSettings: null, trackerSettings: null,
            log: _services.Log, clock: _services.Clock, isOrgMember: (sid, handle) => _services.Database.IsOrgMember(sid, handle));
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

    /// <summary>
    /// Capture thread: asks the UI thread to write the brackets window's "Overlay render" line (its frames, redraws and cost since the last time) to the log. Called right after the
    /// once-a-minute "Live detection" line (and the one at the end of a run), so the two lines come together and the render statistics are not written at any other time.
    /// </summary>
    private void LogRenderSummary()
    {
        if (_disposed)
            return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed)
                _overlay.LogRenderSummary();
        }));
    }

    private void Log(string text) => _services.Log.Write(_services.Clock.UtcNow, text);
}
