namespace Iris.Core.Live;

/// <summary>
/// The numbers of the live detection loop that are not user settings. (The one the user can change, how often the brackets move, is
/// <see cref="Iris.Core.Settings.AppSettings.NameplateTrackingFps"/>.)
/// </summary>
public sealed record LiveSettings
{
    public static LiveSettings Default { get; } = new();

    /// <summary>
    /// How many times a second the screen is read (capture, detector, tracker). 5 normally. A read that takes longer than the period is not
    /// followed by a catch-up: the slots it overran are skipped, reads never queue up.
    /// </summary>
    public double DetectionRateHz { get; init; } = 5.0;

    /// <summary>The period of a read in milliseconds.</summary>
    public double DetectionPeriodMs => 1000.0 / DetectionRateHz;

    /// <summary>
    /// A confirmed track that no read has seen for this long (3 reads at the normal rate) is taken off the overlay, though the tracker keeps the track
    /// for its own expiry (<see cref="Iris.Core.Detection.NameplateTrackerSettings.ExpireMs"/>, 1 s) in case the plate comes back. The same time hides the whole
    /// overlay when no read at all has happened for this long (the loop stalled, the capture was lost): brackets are never left hanging in the air.
    /// </summary>
    public double StaleHideMs { get; init; } = 600.0;

    /// <summary>
    /// Brackets are predicted at most this far ahead of the last time their plate was seen (the tracker uses the same 500 ms for its velocity,
    /// <see cref="Iris.Core.Detection.NameplateTrackerSettings.MaxSpeedDtMs"/>). Beyond it they stand still.
    /// </summary>
    public double MaxPredictionMs { get; init; } = 500.0;

    /// <summary>
    /// A track slower than this (pixels per millisecond: 10 px a second) is taken as standing still: a plate at rest is read at the same place give or take a
    /// pixel, and that jitter must not become a drift of the bracket between two reads.
    /// </summary>
    public double MinSpeedPxPerMs { get; init; } = 0.01;

    /// <summary>
    /// A bracket is never predicted further from the place its plate was last seen than this many heights of its own box. A plate that jumped
    /// (the tracker continues a track that reads the same name far away) has a huge "velocity" from two positions that are far apart; this keeps the bracket calm.
    /// </summary>
    public double MaxShiftBoxHeights { get; init; } = 3.0;

    /// <summary>How long the orgs of a handle, read from the cache, are remembered by the loop before the cache is asked again.</summary>
    public double OrgCacheMs { get; init; } = 2000.0;

    /// <summary>How long capture may stay idle (the game not in front, the overlay disabled) before the screen duplication is released.</summary>
    public double IdleReleaseMs { get; init; } = 5000.0;

    /// <summary>A summary line goes to the log this often while detection runs (and one when it stops).</summary>
    public double SummaryEveryMs { get; init; } = 60_000.0;

    public string? Validate()
    {
        if (!(DetectionRateHz > 0 && DetectionRateHz <= 60))
            return "DetectionRateHz must be above 0 and at most 60.";
        if (StaleHideMs < 0 || MaxPredictionMs < 0 || MinSpeedPxPerMs < 0 || MaxShiftBoxHeights < 0 || OrgCacheMs < 0 || IdleReleaseMs < 0 || SummaryEveryMs <= 0)
            return "The times and limits must not be negative (SummaryEveryMs must be positive).";
        return null;
    }
}
