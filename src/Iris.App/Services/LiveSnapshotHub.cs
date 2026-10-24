using System.Threading;
using Iris.Core.Live;

namespace Iris.App.Services;

/// <summary>
/// The hand-over from the capture loop (which tracks the plates 15 to 120 times a second and publishes what it knows) to the renderer (the UI thread, once per monitor
/// frame): ONE immutable <see cref="LiveSnapshot"/>, replaced as a whole. The capture loop is the only writer, the render hook reads it every frame without a lock;
/// <see cref="LivePredictor"/> moves the brackets of whatever snapshot is there on to the time of the frame. Thread safe.
/// </summary>
public sealed class LiveSnapshotHub
{
    private LiveSnapshot _current = LiveSnapshot.Empty;

    /// <summary>The latest snapshot (<see cref="LiveSnapshot.Empty"/> before the first one and after the game left the front).</summary>
    public LiveSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Capture loop: makes <paramref name="snapshot"/> the one the renderer uses from its next frame on.</summary>
    public void Publish(LiveSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
