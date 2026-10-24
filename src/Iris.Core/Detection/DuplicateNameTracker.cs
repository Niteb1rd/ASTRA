using Iris.Core.Cache;
using Iris.Core.Config;
using Iris.Core.Rsi;

namespace Iris.Core.Detection;

/// <summary>One nameplate the detector found in a frame: the text it read and the rectangle on screen (pixels).</summary>
public readonly record struct DetectedNameplate(string Name, int X, int Y, int Width, int Height);

/// <summary>
/// Finds names that are not players by watching for impossible repeats: a player cannot be in two places at once,
/// so a name that keeps showing up on two separate nameplates in the same frame is probably a sign, a station, a ship...
///
/// For every frame, each valid handle (4 to 64 characters) found on TWO OR MORE separate nameplates counts one "frame" for that name.
/// Rectangles that touch or overlap count as the same nameplate, because the detector sometimes reports one nameplate twice.
/// Counts are kept in the cache database, so they add up across sessions. When a name reaches
/// <see cref="FramesNeeded"/> frames it is added to the exclusion list (<see cref="ExclusionList.AddAutomatic"/>)
/// and its count is deleted.
///
/// Names that are already excluded, or are on the user's player list, are never counted ("the player list wins").
/// One instance is meant to be used from one thread.
/// </summary>
public sealed class DuplicateNameTracker
{
    /// <summary>How many frames with a repeated name it takes before the name is excluded.</summary>
    public const int FramesNeeded = 3;

    private readonly CacheDatabase _db;
    private readonly ExclusionList _exclusions;
    private readonly IClock _clock;

    // The last frame we saw and the names already counted for it, so the same frame twice counts once.
    private long? _lastFrameId;
    private readonly HashSet<string> _countedInLastFrame = new(StringComparer.OrdinalIgnoreCase);

    public DuplicateNameTracker(CacheDatabase db, ExclusionList exclusions, IClock clock)
    {
        _db = db;
        _exclusions = exclusions;
        _clock = clock;
    }

    /// <summary>
    /// Looks at one analysed frame. <paramref name="frameId"/> identifies the frame (a counter or a timestamp);
    /// giving the same id again does not count the same name again.
    /// Returns the names that were added to the exclusion list because of this frame (usually none).
    /// </summary>
    public IReadOnlyList<string> Process(long frameId, IReadOnlyList<DetectedNameplate> nameplates, SocialConfig config)
    {
        if (_lastFrameId != frameId)
        {
            _lastFrameId = frameId;
            _countedInLastFrame.Clear();
        }

        var added = new List<string>();
        var byName = new Dictionary<string, List<DetectedNameplate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var plate in nameplates)
        {
            var name = plate.Name?.Trim();
            if (name is null || !RsiNames.IsValidHandle(name))
                continue;
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = new List<DetectedNameplate>();
            list.Add(plate);
        }

        foreach (var (name, plates) in byName)
        {
            if (plates.Count < 2 || CountSeparateNameplates(plates) < 2)
                continue;
            if (_exclusions.IsExcluded(name) || NameFilter.IsOnPlayerList(name, config))
                continue;
            if (!_countedInLastFrame.Add(name))
                continue;

            var frames = _db.IncrementDuplicateFrames(name, _clock.UtcNow);
            if (frames < FramesNeeded)
                continue;

            var reason = $"seen on 2+ separate nameplates in {FramesNeeded} frames";
            var wasAdded = _exclusions.AddAutomatic(name, reason);
            if (wasAdded || _exclusions.IsExcluded(name))
                _db.DeleteDuplicateFrames(name);
            if (wasAdded)
                added.Add(name);
        }

        return added;
    }

    /// <summary>
    /// How many different nameplates these rectangles make up, where rectangles that touch or overlap
    /// (also through a chain of other rectangles) are one nameplate.
    /// </summary>
    private static int CountSeparateNameplates(List<DetectedNameplate> plates)
    {
        var parent = new int[plates.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (int i = 0; i < plates.Count; i++)
        for (int j = i + 1; j < plates.Count; j++)
            if (TouchOrOverlap(plates[i], plates[j]))
                parent[Find(i)] = Find(j);

        int groups = 0;
        for (int i = 0; i < parent.Length; i++)
            if (Find(i) == i)
                groups++;
        return groups;
    }

    private static bool TouchOrOverlap(DetectedNameplate a, DetectedNameplate b) =>
        (long)a.X <= (long)b.X + b.Width && (long)b.X <= (long)a.X + a.Width
        && (long)a.Y <= (long)b.Y + b.Height && (long)b.Y <= (long)a.Y + a.Height;
}
