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
    /// A velocity is only taken from two sightings at most this far apart (the tracker uses the same 500 ms for its velocity,
    /// <see cref="Iris.Core.Detection.NameplateTrackerSettings.MaxSpeedDtMs"/>), and the template tracking never predicts a position further ahead
    /// than this from the last time it knew one.
    /// </summary>
    public double MaxPredictionMs { get; init; } = 500.0;

    /// <summary>
    /// Rendering at monitor refresh: each frame the brackets are moved from their last TRACKED position along their tracked velocity to "now", but at most
    /// this far ahead of that position's time (<see cref="LivePredictor.Shift"/>). The tracking ticks keep the tracked positions fresh (15 to 120 a second),
    /// so this only fills the gap up to the next tick and never runs away when tracking stalls.
    /// </summary>
    public double RenderExtrapolationMs { get; init; } = 50.0;

    /// <summary>Template tracking: the best match score (TM_CCORR_NORMED, 0 to 1) from which a plate counts as found in a tick.</summary>
    public double TrackMatchThreshold { get; init; } = PlateTemplateTracker.DefaultMatchThreshold;

    /// <summary>Template tracking: a plate not found for this long is hidden until a detection finds it again.</summary>
    public double TrackLostMs { get; init; } = 300.0;

    /// <summary>Template tracking: at most this many plates get a template and are matched every tick (the others are moved by their velocity only).</summary>
    public int MaxTrackedPlates { get; init; } = 24;

    /// <summary>Template tracking: the search patch is the template box grown by at least this many pixels on every side (at 1440 high, scaled with the window height).</summary>
    public double TrackMinMarginPx { get; init; } = 16.0;

    /// <summary>Template tracking: the fastest a plate is assumed to move, in pixels a second at 1440 high (scaled with the window height); it sets the search margin (speed times tick) and caps velocities.</summary>
    public double TrackMaxSpeedPxPerSec { get; init; } = 2000.0;

    /// <summary>Template tracking: the velocity of a plate is taken from its last this-many tracked positions (2 to 3 ticks are smoothed).</summary>
    public int TrackVelocitySamples { get; init; } = 3;

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
        if (RenderExtrapolationMs < 0 || TrackLostMs < 0 || TrackMinMarginPx < 0 || TrackMaxSpeedPxPerSec < 0)
            return "The render and tracking times and distances must not be negative.";
        if (!(TrackMatchThreshold > 0 && TrackMatchThreshold <= 1))
            return "TrackMatchThreshold must be above 0 and at most 1.";
        if (MaxTrackedPlates < 1 || TrackVelocitySamples < 2)
            return "MaxTrackedPlates must be at least 1 and TrackVelocitySamples at least 2.";
        return null;
    }
}
