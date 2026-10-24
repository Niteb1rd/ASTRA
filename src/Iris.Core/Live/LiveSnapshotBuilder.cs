using Iris.Core.Config;
using Iris.Core.Detection;
using Iris.Core.Overlay;
using Iris.Core.Social;

namespace Iris.Core.Live;

/// <summary>
/// Turns the tracks of the <see cref="NameplateTracker"/> into what the overlay shows: a bracket for every track that is a Nameplate with a CONFIRMED name,
/// colored by <see cref="ColorResolver"/> (through <see cref="OverlayModelBuilder"/>, the same code as the test overlay), and the panel entries. It is called after
/// every read, so a track that was corrected (<see cref="TrackerUpdate.Corrected"/>) changes color and bracket shape at once, and a track that expired is gone.
/// Names the <see cref="NameFilter"/> ignores (not a valid handle, the exclusion list, contact codes; the user's player list wins over the last two) get no bracket.
/// A track that no read has seen for <see cref="LiveSettings.StaleHideMs"/> gets none either. Pure logic, no images, no clocks of its own.
/// </summary>
public sealed class LiveSnapshotBuilder
{
    private readonly ExclusionList _exclusions;
    private readonly Func<string, IReadOnlyList<OrgMembership>?> _lookupOrgs;
    private readonly LiveSettings _settings;

    /// <param name="exclusions">The exclusion list.</param>
    /// <param name="lookupOrgs">The orgs of a handle from the cache; null = never looked up (the panel then says "Orgs: not looked up", and only the player list can match).</param>
    public LiveSnapshotBuilder(ExclusionList exclusions, Func<string, IReadOnlyList<OrgMembership>?> lookupOrgs, LiveSettings? settings = null)
    {
        _exclusions = exclusions;
        _lookupOrgs = lookupOrgs;
        _settings = settings ?? LiveSettings.Default;
    }

    /// <summary>
    /// <paramref name="originX"/> and <paramref name="originY"/> turn the tracks' coordinates (crop pixels) into game-window pixels
    /// (<see cref="ScanGeometry.Capture"/>). <paramref name="previous"/> only lets the panel keep its instance when its content did not change.
    /// </summary>
    public LiveSnapshot Build(IReadOnlyList<NameplateTrack> tracks, SocialConfig config, int originX, int originY, double nowMs, LiveSnapshot? previous = null)
    {
        var shown = new List<(NameplateTrack Track, string Name)>();
        foreach (var track in tracks)
        {
            if (track.Verdict != TrackVerdict.Nameplate || track.ConfirmedName is not { } name)
                continue;
            if (nowMs - track.LastSeenMs > _settings.StaleHideMs)
                continue;
            if (NameFilter.ShouldIgnore(name, _exclusions, config))
                continue;
            shown.Add((track, name));
        }

        if (shown.Count == 0)
            return new LiveSnapshot(Array.Empty<LiveBracket>(), Array.Empty<PanelEntry>(), nowMs);

        // first seen first: the panel keeps players of one kind in the order they appeared
        shown.Sort((a, b) => a.Track.Id.CompareTo(b.Track.Id));
        var detected = new List<DetectedPlayer>(shown.Count);
        foreach (var (track, name) in shown)
            detected.Add(new DetectedPlayer(name, WindowBox(track, originX, originY)));

        var model = OverlayModelBuilder.Build(detected, config, _lookupOrgs);
        var brackets = new List<LiveBracket>(shown.Count);
        for (var i = 0; i < shown.Count; i++) // the model has one bracket per detection, in order
        {
            var (track, name) = shown[i];
            var motion = track.Motion(_settings.MaxPredictionMs);
            var item = model.Brackets[i];
            brackets.Add(new LiveBracket(track.Id, name, item.Box, motion.VxPerMs, motion.VyPerMs, motion.AtMs, item.Kind, item.Color));
        }

        var panel = previous is not null && previous.Panel.SequenceEqual(model.Panel) ? previous.Panel : model.Panel;
        return new LiveSnapshot(brackets, panel, nowMs);
    }

    /// <summary>The name line and the distance line of a track as one box, in game-window pixels.</summary>
    public static PixelRect WindowBox(NameplateTrack track, int originX, int originY)
    {
        var top = track.Top;
        var bottom = track.Bottom;
        var left = Math.Min(top.X, bottom.X);
        var right = Math.Max(top.X + top.Width, bottom.X + bottom.Width);
        var upper = Math.Min(top.Y, bottom.Y);
        var lower = Math.Max(top.Y + top.Height, bottom.Y + bottom.Height);
        return new PixelRect(left + originX, upper + originY, right - left, lower - upper);
    }
}
