using Iris.Core.Overlay;
using OpenCvSharp;

namespace Iris.Core.Live;

/// <summary>The job the capture loop hands to the detection thread: the scan region's picture, where the game window was and when the picture was taken.</summary>
public sealed record DetectionJob(Mat Image, PixelRect Window, double CaptureMs, int Epoch);

/// <summary>
/// The hand-over from the capture loop to the detection thread: ONE picture at a time, so that OCR can never hold up the tracking. The capture loop copies the scan region
/// into the mailbox's own buffer (<see cref="TryPost"/>) only when the detection thread is idle (nothing waits, nothing is being read); otherwise the slot is skipped, the
/// capture loop goes on tracking. The detection thread takes the picture (<see cref="TryTake"/>), reads it, and says it is done (<see cref="Complete"/>); the buffer is
/// its own until then. Thread safe.
/// </summary>
public sealed class DetectionMailbox : IDisposable
{
    private readonly object _gate = new();
    private readonly Mat _buffer = new();
    private DetectionJob? _pending;
    private bool _busy;
    private bool _disposed;

    /// <summary>True when a picture could be posted now (none waits and none is being read).</summary>
    public bool IsFree
    {
        get
        {
            lock (_gate)
                return !_disposed && _pending is null && !_busy;
        }
    }

    /// <summary>Copies <paramref name="image"/> into the mailbox. False (nothing copied) when the detection thread has not finished with the last one.</summary>
    public bool TryPost(Mat image, PixelRect window, double captureMs, int epoch)
    {
        lock (_gate)
        {
            if (_disposed || _pending is not null || _busy)
                return false;
            image.CopyTo(_buffer);
            _pending = new DetectionJob(_buffer, window, captureMs, epoch);
            return true;
        }
    }

    /// <summary>Detection thread: takes the waiting picture, if any. The job's image stays valid until <see cref="Complete"/>.</summary>
    public bool TryTake(out DetectionJob? job)
    {
        lock (_gate)
        {
            job = _pending;
            if (_disposed || job is null)
                return false;
            _pending = null;
            _busy = true;
            return true;
        }
    }

    /// <summary>Detection thread: done with the picture taken last; the next one may be posted.</summary>
    public void Complete()
    {
        lock (_gate)
            _busy = false;
    }

    /// <summary>Drops the waiting picture (the game left the front). A picture that is being read is not touched.</summary>
    public void Clear()
    {
        lock (_gate)
            _pending = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = null;
            if (!_busy)
                _buffer.Dispose(); // a busy buffer is left to the garbage collector: the detection thread is still reading it
        }
    }
}

/// <summary>
/// The way back: the detection thread posts what a read found, the capture loop takes it at the start of its next tick. One result at most: a newer one replaces an older one
/// that was not taken (the seeds of the replaced one are disposed). Thread safe.
/// </summary>
public sealed class DetectionResultSlot
{
    private readonly object _gate = new();
    private DetectionResult? _latest;

    public void Post(DetectionResult result)
    {
        DetectionResult? replaced;
        lock (_gate)
        {
            replaced = _latest;
            _latest = result;
        }

        replaced?.Dispose();
    }

    /// <summary>The waiting result, if any; the caller owns it.</summary>
    public bool TryTake(out DetectionResult? result)
    {
        lock (_gate)
        {
            result = _latest;
            _latest = null;
            return result is not null;
        }
    }

    /// <summary>Drops (and disposes) the waiting result.</summary>
    public void Clear()
    {
        DetectionResult? dropped;
        lock (_gate)
        {
            dropped = _latest;
            _latest = null;
        }

        dropped?.Dispose();
    }
}

/// <summary>
/// The last <paramref name="capacity"/> sets of something that is published again and again by one thread and read by another (the brackets as the overlay drew them, for
/// IRIS to black out in its own captures: the screen may still show an earlier frame than the newest). Each set is an array that is never changed after it is published;
/// the history itself is replaced as a whole, so a reader always sees a consistent list. Newest first. Thread safe (publishers are serialized by a lock, readers do not lock).
/// </summary>
public sealed class PublishedHistory<T>
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private T[][] _sets = Array.Empty<T[]>();

    public PublishedHistory(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>The sets published last, newest first (never null; the arrays must not be changed).</summary>
    public IReadOnlyList<T[]> Recent => Volatile.Read(ref _sets);

    /// <summary>Publishes a new set (it must not be changed afterwards). An EMPTY set after an empty set adds nothing: an idle overlay does not wash the history out.</summary>
    public void Publish(T[] set)
    {
        lock (_gate)
        {
            var current = _sets;
            if (set.Length == 0 && current.Length > 0 && current[0].Length == 0)
                return;
            var count = Math.Min(_capacity, current.Length + 1);
            var next = new T[count][];
            next[0] = set;
            Array.Copy(current, 0, next, 1, count - 1);
            Volatile.Write(ref _sets, next);
        }
    }

    /// <summary>Forgets everything (the window that drew the sets is gone, or no longer needs masking).</summary>
    public void Clear()
    {
        lock (_gate)
            Volatile.Write(ref _sets, Array.Empty<T[]>());
    }
}
