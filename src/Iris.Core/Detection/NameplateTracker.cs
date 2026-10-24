using OpenCvSharp;

namespace Iris.Core.Detection;

/// <summary>The sticky verdict of a track.</summary>
public enum TrackVerdict
{
    /// <summary>
    /// The pair was evaluated and is no nameplate (or no longer looks like one). It is not evaluated again every frame: only every
    /// <see cref="NameplateTrackerSettings.RecheckMs"/>, or when its box size changed notably.
    /// </summary>
    NotNameplate,

    /// <summary>The lower line is a distance anchor and the upper line is one word of name characters; names are read and voted on.</summary>
    Nameplate,
}

/// <summary>The tunable numbers of <see cref="NameplateTracker"/>. All times are in milliseconds of video time (the caller's clock).</summary>
public sealed record NameplateTrackerSettings
{
    public static NameplateTrackerSettings Default { get; } = new();

    /// <summary>
    /// A name is CONFIRMED when it has been read identically at least this many times by one track and more often than any other name
    /// of the track. The "delay over wrong" rule: a single misread never becomes a name.
    /// </summary>
    public int ConfirmReads { get; init; } = 3;

    /// <summary>
    /// The identical reads of a name must also span at least this long (from the first to the last of them). The game animates a
    /// nameplate when it appears: the name is typed out or scrambled for a moment, and a stage of that animation can be read twice
    /// in a row. Real names stay.
    /// </summary>
    public double ConfirmSpanMs { get; init; } = 300.0;

    /// <summary>
    /// STABLE BOX: the identical reads that confirm a name must also have the same box WIDTH, within this many pixels (measured on a frame
    /// <see cref="ReferenceFrameHeight"/> high, scaled to the real frame height). While a plate types its name out the box grows by a letter
    /// (about 9 px at 1440p for an average letter) per stage. On the 13 clips (10 fps), for the labeled names that a track read at least twice,
    /// the widths of those reads spread (max minus min) by 0 or 1 px in 92 % of the cases, by 2 px in 4 % and by 3 px or more in 4 % (a plate that moves toward or away from
    /// the camera changes its size a little: "TITANIUM" went 69, 73, 72 px in three frames). 3 px, a third of an average letter, keeps the settled
    /// names and still rejects the growth of a typed-out name by a letter or more.
    /// </summary>
    public double StableWidthPx { get; init; } = 3.0;

    /// <summary>The frame height <see cref="StableWidthPx"/> is given for.</summary>
    public int ReferenceFrameHeight { get; init; } = 1440;

    /// <summary>
    /// CORRECTIONS: once a name is confirmed, only the votes of the last this-many milliseconds count. When another name meets the confirmation
    /// rule there (enough identical reads of one box width, spread over <see cref="ConfirmSpanMs"/>, and more votes in the window than ANY other name
    /// has, the confirmed name included), the confirmed name switches to it. A confirmed track whose latest read disagrees with its name is read at every frame (not only every
    /// <see cref="ConfirmedRereadMs"/>) until it settles.
    /// </summary>
    public double CorrectionWindowMs { get; init; } = 4000.0;

    /// <summary>
    /// BEFORE the first confirmation only the votes of the last this-many milliseconds count: 4 times <see cref="ConfirmSpanMs"/>. A confirmation
    /// needs identical reads that span <see cref="ConfirmSpanMs"/>, so a vote much older than that cannot be part of one, and without the cap a
    /// plate that never confirms (two names that alternate, a flickering read) would keep every vote it ever got. Older votes are dropped (and no
    /// longer count as rivals). With a <see cref="ConfirmSpanMs"/> of 0 there is no cap (infinity).
    /// </summary>
    public double PreConfirmationWindowMs => ConfirmSpanMs > 0 ? 4.0 * ConfirmSpanMs : double.PositiveInfinity;

    /// <summary>A track that no candidate matched for this long is closed.</summary>
    public double ExpireMs { get; init; } = 1000.0;

    /// <summary>A NotNameplate track is evaluated again after this long (it may have turned into a nameplate; a name can be read again later).</summary>
    public double RecheckMs { get; init; } = 3000.0;

    /// <summary>A track with a confirmed name is read again only this often (it is still followed, and expires as usual).</summary>
    public double ConfirmedRereadMs { get; init; } = 1000.0;

    /// <summary>This many evaluations in a row that say "no nameplate" turn a Nameplate track into NotNameplate.</summary>
    public int MaxFailStreak { get; init; } = 3;

    /// <summary>A track at rest moves at most this many line heights between two frames (the resting gate around its last position).</summary>
    public double MoveLines { get; init; } = 3.0;

    /// <summary>
    /// The gate around the position a track is expected at (its last position plus its last velocity times the time since it was seen)
    /// is <see cref="MoveLines"/> plus this factor times the distance the track is expected to have moved: a fast track gets a wider gate.
    /// </summary>
    public double SpeedGain { get; init; } = 1.0;

    /// <summary>The gate never grows beyond this many line heights.</summary>
    public double MaxGateLines { get; init; } = 10.0;

    /// <summary>
    /// The prediction looks ahead at most this long, and the velocity of a track is taken only from two associations at most this far
    /// apart in time (otherwise it counts as standing still).
    /// </summary>
    public double MaxSpeedDtMs { get; init; } = 500.0;

    /// <summary>
    /// A candidate that no track's gate holds but that reads the same name as a Nameplate track seen within this long, and has the size of
    /// that track's name line, continues that track however far it jumped (a fast mover, a camera cut): the same text on the same sized
    /// plate is the same plate.
    /// </summary>
    public double IdenticalMs { get; init; } = 500.0;

    /// <summary>Same object: the name line's width differs by at most this fraction (plus half a line height), its height by at most max(2 px, 25 %).</summary>
    public double WidthTolerance { get; init; } = 0.25;

    /// <summary>"Box size changed notably": the width by more than this fraction (plus 1 px), or the height by max(2 px, this fraction).</summary>
    public double SizeChange { get; init; } = 0.15;

    /// <summary>At most this many tracks are kept; the least recently seen are dropped first.</summary>
    public int MaxTracks { get; init; } = 256;

    /// <summary>At most this many candidates are evaluated (read) per frame; the others wait for the next frame.
    /// Soft: it counts the evaluations the tracker asks for; the neighbour check of a crowd pair (<see cref="FrameEvaluator"/>) can read further pairs inside one of them.</summary>
    public int MaxEvaluationsPerFrame { get; init; } = 24;

    /// <summary>Returns a description of the first problem with the values, or null if they are usable.</summary>
    public string? Validate()
    {
        if (ConfirmReads < 1)
            return "ConfirmReads must be at least 1.";
        if (ExpireMs < 0 || RecheckMs < 0 || ConfirmedRereadMs < 0 || ConfirmSpanMs < 0 || CorrectionWindowMs < 0)
            return "The times must not be negative.";
        if (StableWidthPx < 0 || ReferenceFrameHeight < 1)
            return "StableWidthPx must not be negative and ReferenceFrameHeight must be at least 1.";
        if (MaxFailStreak < 1 || MaxTracks < 1 || MaxEvaluationsPerFrame < 1)
            return "MaxFailStreak, MaxTracks and MaxEvaluationsPerFrame must be at least 1.";
        if (MoveLines <= 0 || WidthTolerance < 0 || SizeChange < 0)
            return "MoveLines must be positive and the tolerances must not be negative.";
        if (SpeedGain < 0 || MaxGateLines <= 0 || MaxSpeedDtMs < 0 || IdenticalMs < 0)
            return "SpeedGain, MaxSpeedDtMs and IdenticalMs must not be negative and MaxGateLines must be positive.";
        return null;
    }
}

/// <summary>
/// A candidate pair of a frame: the line that may be a name and the line right below it.
/// </summary>
/// <param name="Top">The name line.</param>
/// <param name="Bottom">The line below it.</param>
/// <param name="Strict">True when both lines are isolated (nothing but specks within three line heights on either side). A pair that is
/// not strict may only continue a track that is already a Nameplate: starting a nameplate takes a clean look, keeping one does not
/// (a ship, a HUD arc or another plate drifting next to it must not make it flicker).</param>
public readonly record struct TrackCandidate(Rect Top, Rect Bottom, bool Strict = true);

/// <summary>
/// What evaluating a candidate found out (see <see cref="NameplateDetector.Evaluate"/>).
/// </summary>
/// <param name="IsNameplate">The lower line is a distance anchor and the upper line is one word of valid name characters.</param>
/// <param name="Name">The name, when it was also read surely enough to count as a vote; otherwise null.</param>
public readonly record struct TrackEvaluation(bool IsNameplate, string? Name)
{
    public static TrackEvaluation No { get; } = new(false, null);
}

/// <summary>One candidate pair followed from frame to frame, with its sticky verdict and its votes.</summary>
public sealed class NameplateTrack
{
    private readonly List<NameRead> _reads = new();
    private readonly Dictionary<string, long> _firstReadFrame = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Frame, double Ms)> _confirmations = new();
    private readonly List<NameVote> _votes = new();
    private readonly List<NameCorrection> _corrections = new();

    internal NameplateTrack(int id, Rect top, Rect bottom, long frame, double timeMs)
    {
        Id = id;
        Top = top;
        Bottom = bottom;
        FirstFrame = frame;
        FirstMs = timeMs;
        LastSeenMs = timeMs;
        LastCheckedMs = timeMs;
        CheckedWidth = top.Width;
        CheckedHeight = top.Height;
    }

    public int Id { get; }

    /// <summary>The name line, as of the last frame that saw the track.</summary>
    public Rect Top { get; internal set; }

    /// <summary>The line below it.</summary>
    public Rect Bottom { get; internal set; }

    public TrackVerdict Verdict { get; internal set; } = TrackVerdict.NotNameplate;

    /// <summary>True when the track was a Nameplate at any time (it passed the distance anchor once).</summary>
    public bool EverNameplate { get; internal set; }

    public long FirstFrame { get; }

    public double FirstMs { get; }

    public double LastSeenMs { get; internal set; }

    public double LastCheckedMs { get; internal set; }

    /// <summary>Width and height of the name line when the track was last evaluated.</summary>
    internal int CheckedWidth { get; set; }

    internal int CheckedHeight { get; set; }

    internal double LastReadMs { get; set; } = double.NegativeInfinity;

    internal int FailStreak { get; set; }

    /// <summary>How often the track was evaluated (read).</summary>
    public int Evaluations { get; internal set; }

    /// <summary>Votes: every name read surely enough, with how often it was read, in the order the names were first read.</summary>
    public IReadOnlyList<NameRead> Reads => _reads;

    public int ReadCount { get; private set; }

    /// <summary>
    /// How many votes the track still holds for the confirmation rule: those inside <see cref="NameplateTrackerSettings.PreConfirmationWindowMs"/> (before
    /// the first confirmation) or <see cref="NameplateTrackerSettings.CorrectionWindowMs"/> (afterwards) of the latest vote. <see cref="ReadCount"/> counts all.
    /// </summary>
    public int VoteWindowCount => _votes.Count;

    /// <summary>The name with the most votes (the first of them on a tie), or null when nothing was read.</summary>
    public string? BestName
    {
        get
        {
            string? best = null;
            var most = 0;
            foreach (var read in _reads)
            {
                if (read.Count > most)
                {
                    best = read.Name;
                    most = read.Count;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// The confirmed name now: read identically at least <see cref="NameplateTrackerSettings.ConfirmReads"/> times at one box width, over at least
    /// <see cref="NameplateTrackerSettings.ConfirmSpanMs"/>, and more often than any other. Null before the first confirmation. Once set it is never
    /// taken back, only SWITCHED to another name that meets the rule over the recent window (<see cref="Corrections"/>).
    /// </summary>
    public string? ConfirmedName { get; private set; }

    /// <summary>The frame at which <see cref="ConfirmedName"/> was confirmed.</summary>
    public long? ConfirmedFrame { get; private set; }

    public double? ConfirmedMs { get; private set; }

    /// <summary>Every confirmation the track made, in order: the first one, and the new name of every correction (a track normally has one).</summary>
    public IReadOnlyList<(string Name, long Frame, double Ms)> Confirmations => _confirmations;

    /// <summary>
    /// Every time the confirmed name switched to another name, in order. A track with corrections showed a name that was later found wrong:
    /// the app updates the panel when <see cref="TrackerUpdate.Corrected"/> lists the track.
    /// </summary>
    public IReadOnlyList<NameCorrection> Corrections => _corrections;

    /// <summary>
    /// True when the latest vote disagrees with the confirmed name (the track is read at every frame until it settles). An evaluation that finds the
    /// nameplate but gives no vote (the read was too unsure, or it starts or ends with <c>-</c>) clears it: the disagreement is no longer the latest word.
    /// </summary>
    public bool IsDisputed { get; private set; }

    internal void ClearDispute() => IsDisputed = false;

    /// <summary>Per name: the frame of its first vote.</summary>
    public IReadOnlyDictionary<string, long> FirstReadFrame => _firstReadFrame;

    /// <summary>How often a candidate with the same read, far from the track's gate, was continued as this track (<see cref="NameplateTrackerSettings.IdenticalMs"/>).</summary>
    public int Jumps { get; internal set; }

    // The two last associations (time, center of the name line): the constant velocity prediction comes from them.
    private bool _hasLast;
    private bool _hasPrevious;
    private (double Ms, double X, double Y) _last;
    private (double Ms, double X, double Y) _previous;

    internal void NotePosition(double timeMs, Rect top)
    {
        _previous = _last;
        _hasPrevious = _hasLast;
        _last = (timeMs, top.X + top.Width / 2.0, top.Y + top.Height / 2.0);
        _hasLast = true;
    }

    internal bool HasLastPosition => _hasLast;

    internal double LastPositionMs => _last.Ms;

    /// <summary>Pixels per millisecond from the last two associations; (0, 0) when unknown or when they are more than <paramref name="maxDtMs"/> apart.</summary>
    internal (double X, double Y) Velocity(double maxDtMs)
    {
        if (!_hasPrevious || !_hasLast)
            return (0.0, 0.0);
        var dt = _last.Ms - _previous.Ms;
        if (dt <= 0 || dt > maxDtMs)
            return (0.0, 0.0);
        return ((_last.X - _previous.X) / dt, (_last.Y - _previous.Y) / dt);
    }

    internal double CenterX => Top.X + Top.Width / 2.0;

    internal double CenterY => Top.Y + Top.Height / 2.0;

    /// <summary>
    /// Adds a vote (a name read surely enough, on a name line <paramref name="width"/> pixels wide) and tells what it changed: nothing, the
    /// first confirmation, or a correction of the confirmed name.
    /// </summary>
    internal VoteOutcome Vote(string name, long frame, double timeMs, int width, NameplateTrackerSettings settings, double widthTolerance)
    {
        var index = _reads.FindIndex(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            _reads.Add(new NameRead(name, 1, timeMs, timeMs));
            _firstReadFrame[name] = frame;
        }
        else
        {
            var old = _reads[index];
            _reads[index] = old with { Count = old.Count + 1, LastMs = timeMs };
        }

        ReadCount++;
        _votes.Add(new NameVote(name, timeMs, width));
        // older votes no longer count, now or later: before the first confirmation only the last 4 x ConfirmSpanMs, afterwards the correction window
        var window = ConfirmedName is null ? settings.PreConfirmationWindowMs : settings.CorrectionWindowMs;
        _votes.RemoveAll(v => v.Ms < timeMs - window);

        var outcome = VoteOutcome.None;
        var trusted = Trusted(timeMs, settings, widthTolerance);
        if (trusted is not null && !string.Equals(trusted, ConfirmedName, StringComparison.Ordinal))
        {
            if (ConfirmedName is not null)
            {
                _corrections.Add(new NameCorrection(ConfirmedName, trusted, frame, timeMs));
                outcome = VoteOutcome.Corrected;
            }
            else
            {
                outcome = VoteOutcome.Confirmed;
            }

            ConfirmedName = trusted;
            ConfirmedFrame = frame;
            ConfirmedMs = timeMs;
            _confirmations.Add((trusted, frame, timeMs));
        }

        IsDisputed = ConfirmedName is not null && !string.Equals(name, ConfirmedName, StringComparison.Ordinal);
        return outcome;
    }

    /// <summary>
    /// The name that meets the confirmation rule now, or null: at least <see cref="NameplateTrackerSettings.ConfirmReads"/> identical reads at the
    /// same box width (within <paramref name="widthTolerance"/>), spread over <see cref="NameplateTrackerSettings.ConfirmSpanMs"/>, and more reads
    /// than any other name. Before the first confirmation every vote counts, afterwards only the votes of the last
    /// <see cref="NameplateTrackerSettings.CorrectionWindowMs"/>.
    /// </summary>
    private string? Trusted(double timeMs, NameplateTrackerSettings settings, double widthTolerance)
    {
        var since = ConfirmedName is null ? double.NegativeInfinity : timeMs - settings.CorrectionWindowMs;
        var byName = new Dictionary<string, List<(double Ms, int Width)>>(StringComparer.Ordinal);
        foreach (var vote in _votes)
        {
            if (vote.Ms < since)
                continue;
            if (!byName.TryGetValue(vote.Name, out var samples))
                byName[vote.Name] = samples = new List<(double, int)>();
            samples.Add((vote.Ms, vote.Width));
        }

        foreach (var (name, samples) in byName)
        {
            var (count, first, last) = StableGroup(samples, widthTolerance);
            var rival = 0;
            foreach (var (other, otherSamples) in byName)
                if (!string.Equals(other, name, StringComparison.Ordinal) && otherSamples.Count > rival)
                    rival = otherSamples.Count;
            if (count >= settings.ConfirmReads && count > rival && last - first >= settings.ConfirmSpanMs)
                return name;
        }

        return null;
    }

    /// <summary>
    /// The largest set of reads (of one name, in time order) whose widths lie within plus or minus <paramref name="tolerance"/> of one of them (the
    /// later set on a tie): its size and the times of its first and last read. O(n log n): the reads are sorted by width and a window of
    /// +-<paramref name="tolerance"/> around each width slides over them, a pair of monotonic queues giving the earliest and the latest time inside it.
    /// "Later" on a tie means: more reads, then the later last read, then the read that comes later in <paramref name="samples"/>. The times must not decrease.
    /// </summary>
    public static (int Count, double First, double Last) StableGroup(IReadOnlyList<(double Ms, int Width)> samples, double tolerance)
    {
        var n = samples.Count;
        if (n == 0 || tolerance < 0)
            return (0, 0.0, 0.0);

        var order = new int[n]; // indices of samples, by width (equal widths: by position)
        for (var i = 0; i < n; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var byWidth = samples[a].Width.CompareTo(samples[b].Width);
            return byWidth != 0 ? byWidth : a.CompareTo(b);
        });

        // the window is order[lo .. added-1]: the reads whose width is within +-tolerance of the current one. Both ends only move forward.
        // minQueue / maxQueue hold positions in `order` whose times are increasing / decreasing: the front is the earliest / latest time of the window.
        var minQueue = new int[n];
        var maxQueue = new int[n];
        int minHead = 0, minTail = 0, maxHead = 0, maxTail = 0;
        var lo = 0;
        var added = 0;
        var best = (Count: 0, First: 0.0, Last: 0.0);
        var bestIndex = -1;
        for (var k = 0; k < n; k++)
        {
            var anchor = samples[order[k]].Width;
            while (anchor - samples[order[lo]].Width > tolerance)
                lo++; // never passes k: the anchor is 0 away from itself
            while (added < n && samples[order[added]].Width - anchor <= tolerance)
            {
                var ms = samples[order[added]].Ms;
                while (maxTail > maxHead && samples[order[maxQueue[maxTail - 1]]].Ms <= ms)
                    maxTail--;
                maxQueue[maxTail++] = added;
                while (minTail > minHead && samples[order[minQueue[minTail - 1]]].Ms >= ms)
                    minTail--;
                minQueue[minTail++] = added;
                added++;
            }

            while (minQueue[minHead] < lo)
                minHead++;
            while (maxQueue[maxHead] < lo)
                maxHead++;

            var count = added - lo;
            var first = samples[order[minQueue[minHead]]].Ms;
            var last = samples[order[maxQueue[maxHead]]].Ms;
            var index = order[k];
            if (count > best.Count
                || (count == best.Count && (last > best.Last || (last == best.Last && index > bestIndex))))
            {
                best = (count, first, last);
                bestIndex = index;
            }
        }

        return best;
    }
}

/// <summary>What a vote changed.</summary>
internal enum VoteOutcome
{
    None,
    Confirmed,
    Corrected,
}

/// <summary>One vote: the name read, when (ms of video time) and how wide the name line was.</summary>
internal readonly record struct NameVote(string Name, double Ms, int Width);

/// <summary>The confirmed name of a track switched from <paramref name="From"/> to <paramref name="To"/> at this frame (ms of video time).</summary>
public sealed record NameCorrection(string From, string To, long Frame, double Ms);

/// <summary>One name a track has read: how often, and when it was first and last read (ms of video time).</summary>
public sealed record NameRead(string Name, int Count, double FirstMs, double LastMs);

/// <summary>What one <see cref="NameplateTracker.Update"/> did.</summary>
/// <param name="Confirmed">Tracks whose confirmed name is new in this frame: a first confirmation, or a correction (their
/// <see cref="NameplateTrack.ConfirmedName"/> is what to show now).</param>
/// <param name="Closed">Tracks that expired in this frame (they are no longer in <see cref="NameplateTracker.Tracks"/>).</param>
/// <param name="Corrected">The part of <paramref name="Confirmed"/> that CHANGED an already confirmed name (the last of
/// <see cref="NameplateTrack.Corrections"/> says from what to what): the app updates the panel of that nameplate.</param>
public sealed record TrackerUpdate(IReadOnlyList<NameplateTrack> Confirmed, IReadOnlyList<NameplateTrack> Closed, IReadOnlyList<NameplateTrack> Corrected);

/// <summary>
/// Follows candidate pairs from frame to frame, keeps a sticky verdict per track and lets the reads of a nameplate vote on its
/// name. A port of <c>tracker_ref.py</c>; no OpenCV image work and no WPF, so it is testable with fake evaluations.
/// <list type="bullet">
/// <item>A candidate is matched to the nearest track of about the same size (closest first, one candidate per track, nearest to the track's
/// PREDICTED position first: constant velocity from its last two positions, with a gate that grows with the track's speed). A candidate that
/// matches no track is evaluated at once; if it reads the same name as a Nameplate track seen within
/// <see cref="NameplateTrackerSettings.IdenticalMs"/> on a name line of the same size, it continues that track (however far it jumped), otherwise it
/// starts a new track. Only strict candidates (both lines isolated, or isolated but for the text of other nameplates) start tracks; a non-strict
/// one can only continue a Nameplate track.</item>
/// <item>A <see cref="TrackVerdict.NotNameplate"/> track is not evaluated again until <see cref="NameplateTrackerSettings.RecheckMs"/> have
/// passed or its box size changed notably. Junk that stays on screen (HUD word pairs, signs) is read once, not every frame.</item>
/// <item>A <see cref="TrackVerdict.Nameplate"/> track is evaluated every frame until it has a confirmed name, then every
/// <see cref="NameplateTrackerSettings.ConfirmedRereadMs"/> (every frame while its latest read disagrees with its name). Each evaluation that reads
/// a name surely enough, and that does not start or end with -, is a vote. A name is confirmed when it has
/// <see cref="NameplateTrackerSettings.ConfirmReads"/> votes at one box width (<see cref="NameplateTrackerSettings.StableWidthPx"/>), more than any
/// other name of the track, spread over at least <see cref="NameplateTrackerSettings.ConfirmSpanMs"/> (before the first confirmation only the votes of the last
/// <see cref="NameplateTrackerSettings.PreConfirmationWindowMs"/> count). A confirmed name is corrected, never
/// withdrawn: when another name meets the rule over the last <see cref="NameplateTrackerSettings.CorrectionWindowMs"/> it replaces the confirmed one
/// (<see cref="TrackerUpdate.Corrected"/>, <see cref="NameplateTrack.Corrections"/>).</item>
/// <item>Several evaluations in a row that say "no nameplate" turn a Nameplate track back into NotNameplate.</item>
/// <item>A track no candidate matched for <see cref="NameplateTrackerSettings.ExpireMs"/> is closed.</item>
/// </list>
/// Not thread safe.
/// </summary>
public sealed class NameplateTracker
{
    private readonly NameplateTrackerSettings _settings;
    private readonly List<NameplateTrack> _tracks = new();
    private int _nextId = 1;

    public NameplateTracker(NameplateTrackerSettings? settings = null)
    {
        _settings = settings ?? NameplateTrackerSettings.Default;
        var problem = _settings.Validate();
        if (problem is not null)
            throw new ArgumentException(problem, nameof(settings));
    }

    /// <summary>The live tracks.</summary>
    public IReadOnlyList<NameplateTrack> Tracks => _tracks;

    /// <summary>
    /// Processes one frame. <paramref name="candidates"/> are the pairs the cheap pass found; <paramref name="evaluate"/> reads one
    /// (top line, bottom line) and says whether it is a nameplate and which name it read. It is called only for the candidates that need it.
    /// <paramref name="frameHeight"/> is the height of the frame in pixels (the stable width tolerance is given for
    /// <see cref="NameplateTrackerSettings.ReferenceFrameHeight"/>); 0 means that height.
    /// </summary>
    public TrackerUpdate Update(long frame, double timeMs, IReadOnlyList<TrackCandidate> candidates, Func<Rect, Rect, TrackEvaluation> evaluate, int frameHeight = 0)
    {
        var tolerance = _settings.StableWidthPx * (frameHeight > 0 ? frameHeight : _settings.ReferenceFrameHeight) / _settings.ReferenceFrameHeight;
        var confirmedNow = new List<NameplateTrack>();
        var correctedNow = new List<NameplateTrack>();
        var evaluations = 0;

        // match candidates and tracks, closest first
        var matches = new List<(double Distance, int Candidate, int Track)>();
        for (var c = 0; c < candidates.Count; c++)
        {
            for (var t = 0; t < _tracks.Count; t++)
            {
                if (!candidates[c].Strict && _tracks[t].Verdict != TrackVerdict.Nameplate)
                    continue;
                var d = SameObject(_tracks[t], candidates[c].Top, timeMs);
                if (d is not null)
                    matches.Add((d.Value, c, t));
            }
        }

        matches.Sort((a, b) =>
        {
            var byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0)
                return byDistance;
            var byCandidate = a.Candidate.CompareTo(b.Candidate);
            return byCandidate != 0 ? byCandidate : a.Track.CompareTo(b.Track);
        });
        var trackOfCandidate = new Dictionary<int, int>();
        var trackUsed = new HashSet<int>();
        foreach (var (_, c, t) in matches)
        {
            if (trackOfCandidate.ContainsKey(c) || trackUsed.Contains(t))
                continue;
            trackOfCandidate[c] = t;
            trackUsed.Add(t);
        }

        var existing = _tracks.Count;
        for (var c = 0; c < candidates.Count; c++)
        {
            var top = candidates[c].Top;
            var bottom = candidates[c].Bottom;
            if (trackOfCandidate.TryGetValue(c, out var index))
            {
                var track = _tracks[index];
                track.LastSeenMs = timeMs;
                track.NotePosition(timeMs, top);
                var sizeChanged = SizeChanged(track, top);
                track.Top = top;
                track.Bottom = bottom;
                if (track.Verdict == TrackVerdict.NotNameplate)
                {
                    if (!(sizeChanged || timeMs - track.LastCheckedMs >= _settings.RecheckMs))
                        continue;
                }
                else if (track.ConfirmedName is not null && !track.IsDisputed && timeMs - track.LastReadMs < _settings.ConfirmedRereadMs && !sizeChanged)
                {
                    continue;
                }

                if (evaluations >= _settings.MaxEvaluationsPerFrame)
                    continue;
                evaluations++;
                Evaluate(track, frame, timeMs, evaluate, confirmedNow, correctedNow, tolerance, null);
            }
            else
            {
                if (!candidates[c].Strict || evaluations >= _settings.MaxEvaluationsPerFrame)
                    continue;
                evaluations++;
                var result = evaluate(top, bottom);
                var readName = result.Name;
                var lost = result.IsNameplate && readName is not null ? FindLostTrack(readName, top, timeMs, existing, trackUsed) : -1;
                NameplateTrack track;
                if (lost >= 0)
                {
                    // the same read on a plate of the same size soon after the track was lost: it jumped, continue the track
                    track = _tracks[lost];
                    trackUsed.Add(lost);
                    track.Jumps++;
                    track.LastSeenMs = timeMs;
                    track.NotePosition(timeMs, top);
                    track.Top = top;
                    track.Bottom = bottom;
                }
                else
                {
                    track = new NameplateTrack(_nextId++, top, bottom, frame, timeMs);
                    track.NotePosition(timeMs, top);
                    _tracks.Add(track);
                }

                Evaluate(track, frame, timeMs, evaluate, confirmedNow, correctedNow, tolerance, result);
            }
        }

        var closed = _tracks.Where(t => timeMs - t.LastSeenMs > _settings.ExpireMs).ToList();
        var remaining = _tracks.Count - closed.Count;
        if (remaining > _settings.MaxTracks)
        {
            var surplus = remaining - _settings.MaxTracks;
            closed.AddRange(_tracks.Where(t => !closed.Contains(t)).OrderBy(t => t.LastSeenMs).Take(surplus).ToList());
        }

        if (closed.Count > 0)
            _tracks.RemoveAll(t => closed.Contains(t));
        return new TrackerUpdate(confirmedNow, closed, correctedNow);
    }

    /// <summary>Closes every remaining track (end of a clip) and returns them.</summary>
    public IReadOnlyList<NameplateTrack> Finish()
    {
        var all = _tracks.ToList();
        _tracks.Clear();
        return all;
    }

    /// <summary>
    /// A Nameplate track made before this frame, not matched in it, seen within <see cref="NameplateTrackerSettings.IdenticalMs"/>, whose best
    /// read is <paramref name="name"/> and whose name line has the size of <paramref name="top"/>: the most recently seen, then the nearest.
    /// Returns its index, or -1. A track is claimed once per frame.
    /// </summary>
    private int FindLostTrack(string name, Rect top, double timeMs, int existing, HashSet<int> trackUsed)
    {
        var best = -1;
        (double Negative, double Distance) bestKey = default;
        var cx = top.X + top.Width / 2.0;
        var cy = top.Y + top.Height / 2.0;
        for (var t = 0; t < existing; t++)
        {
            var track = _tracks[t];
            if (trackUsed.Contains(t) || track.Verdict != TrackVerdict.Nameplate || timeMs - track.LastSeenMs > _settings.IdenticalMs)
                continue;
            if (!string.Equals(track.BestName, name, StringComparison.Ordinal) || !SameSize(track, top))
                continue;
            var key = (-track.LastSeenMs, (cx - track.CenterX) * (cx - track.CenterX) + (cy - track.CenterY) * (cy - track.CenterY));
            if (best < 0 || key.Item1 < bestKey.Negative || (key.Item1 == bestKey.Negative && key.Item2 < bestKey.Distance))
            {
                best = t;
                bestKey = key;
            }
        }

        return best;
    }

    private void Evaluate(NameplateTrack track, long frame, double timeMs, Func<Rect, Rect, TrackEvaluation> evaluate, List<NameplateTrack> confirmedNow, List<NameplateTrack> correctedNow, double tolerance, TrackEvaluation? known)
    {
        track.Evaluations++;
        track.LastCheckedMs = timeMs;
        track.CheckedWidth = track.Top.Width;
        track.CheckedHeight = track.Top.Height;
        var result = known ?? evaluate(track.Top, track.Bottom);
        if (!result.IsNameplate)
        {
            if (track.Verdict == TrackVerdict.Nameplate)
            {
                track.FailStreak++;
                if (track.FailStreak >= _settings.MaxFailStreak)
                    track.Verdict = TrackVerdict.NotNameplate;
            }

            return;
        }

        track.Verdict = TrackVerdict.Nameplate;
        track.EverNameplate = true;
        track.FailStreak = 0;
        track.LastReadMs = timeMs;
        if (result.Name is null || !NameShape.HasValidEdges(result.Name))
        {
            track.ClearDispute(); // a nameplate without a vote (too unsure, or a name may not start or end with -): no disagreement to chase
            return;
        }

        var outcome = track.Vote(result.Name, frame, timeMs, track.Top.Width, _settings, tolerance);
        if (outcome != VoteOutcome.None)
            confirmedNow.Add(track);
        if (outcome == VoteOutcome.Corrected)
            correctedNow.Add(track);
    }

    /// <summary>The name line has about the size of the track's: height within max(2 px, 25 %), width within the tolerance (plus half a line height).</summary>
    private bool SameSize(NameplateTrack track, Rect top)
    {
        var th = track.Top.Height;
        if (Math.Abs(top.Height - th) > Math.Max(2.0, 0.25 * th))
            return false;
        return Math.Abs(top.Width - track.Top.Width) <= _settings.WidthTolerance * track.Top.Width + 0.5 * th;
    }

    /// <summary>
    /// The distance (pixels) at which <paramref name="top"/> can be the track's name line, or null when it cannot. It must have the size of
    /// the track's line and lie either within the resting gate (<see cref="NameplateTrackerSettings.MoveLines"/> line heights) of the track's last
    /// position, or within the wider gate of the position expected from the constant velocity of the track's last two associations
    /// (the gate grows with the distance the track is expected to have moved, up to <see cref="NameplateTrackerSettings.MaxGateLines"/> line heights).
    /// The smaller of the two distances is returned.
    /// </summary>
    private double? SameObject(NameplateTrack track, Rect top, double timeMs)
    {
        if (!SameSize(track, top))
            return null;
        var th = track.Top.Height;
        var cx = top.X + top.Width / 2.0;
        var cy = top.Y + top.Height / 2.0;
        var dLast = Math.Sqrt((cx - track.CenterX) * (cx - track.CenterX) + (cy - track.CenterY) * (cy - track.CenterY));
        var rest = _settings.MoveLines * th;
        double? best = dLast <= rest ? dLast : null;
        if (track.HasLastPosition)
        {
            var (vx, vy) = track.Velocity(_settings.MaxSpeedDtMs);
            var dt = Math.Min(Math.Max(0.0, timeMs - track.LastPositionMs), _settings.MaxSpeedDtMs);
            var px = track.CenterX + vx * dt;
            var py = track.CenterY + vy * dt;
            var moved = Math.Sqrt((vx * dt) * (vx * dt) + (vy * dt) * (vy * dt));
            var gate = Math.Min(rest + _settings.SpeedGain * moved, _settings.MaxGateLines * th);
            var dPred = Math.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
            if (dPred <= gate && (best is null || dPred < best))
                best = dPred;
        }

        return best;
    }

    private bool SizeChanged(NameplateTrack track, Rect top) =>
        Math.Abs(top.Width - track.CheckedWidth) > _settings.SizeChange * track.CheckedWidth + 1
        || Math.Abs(top.Height - track.CheckedHeight) >= Math.Max(2.0, _settings.SizeChange * track.CheckedHeight);
}
