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

    /// <summary>A track that no candidate matched for this long is closed.</summary>
    public double ExpireMs { get; init; } = 1000.0;

    /// <summary>A NotNameplate track is evaluated again after this long (it may have turned into a nameplate; a name can be read again later).</summary>
    public double RecheckMs { get; init; } = 3000.0;

    /// <summary>A track with a confirmed name is read again only this often (it is still followed, and expires as usual).</summary>
    public double ConfirmedRereadMs { get; init; } = 1000.0;

    /// <summary>This many evaluations in a row that say "no nameplate" turn a Nameplate track into NotNameplate.</summary>
    public int MaxFailStreak { get; init; } = 3;

    /// <summary>A track moves at most this many line heights between two frames.</summary>
    public double MoveLines { get; init; } = 3.0;

    /// <summary>Same object: the name line's width differs by at most this fraction (plus half a line height), its height by at most max(2 px, 25 %).</summary>
    public double WidthTolerance { get; init; } = 0.25;

    /// <summary>"Box size changed notably": the width by more than this fraction (plus 1 px), or the height by max(2 px, this fraction).</summary>
    public double SizeChange { get; init; } = 0.15;

    /// <summary>At most this many tracks are kept; the least recently seen are dropped first.</summary>
    public int MaxTracks { get; init; } = 256;

    /// <summary>At most this many candidates are evaluated (read) per frame; the others wait for the next frame.</summary>
    public int MaxEvaluationsPerFrame { get; init; } = 24;

    /// <summary>Returns a description of the first problem with the values, or null if they are usable.</summary>
    public string? Validate()
    {
        if (ConfirmReads < 1)
            return "ConfirmReads must be at least 1.";
        if (ExpireMs < 0 || RecheckMs < 0 || ConfirmedRereadMs < 0 || ConfirmSpanMs < 0)
            return "The times must not be negative.";
        if (MaxFailStreak < 1 || MaxTracks < 1 || MaxEvaluationsPerFrame < 1)
            return "MaxFailStreak, MaxTracks and MaxEvaluationsPerFrame must be at least 1.";
        if (MoveLines <= 0 || WidthTolerance < 0 || SizeChange < 0)
            return "MoveLines must be positive and the tolerances must not be negative.";
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

    /// <summary>The confirmed name now: read at least <see cref="NameplateTrackerSettings.ConfirmReads"/> times and more often than any other. Null before, and again null if a rival catches up.</summary>
    public string? ConfirmedName { get; private set; }

    /// <summary>The frame at which <see cref="ConfirmedName"/> was confirmed.</summary>
    public long? ConfirmedFrame { get; private set; }

    public double? ConfirmedMs { get; private set; }

    /// <summary>Every confirmation the track made, in order (a track normally has at most one).</summary>
    public IReadOnlyList<(string Name, long Frame, double Ms)> Confirmations => _confirmations;

    /// <summary>Per name: the frame of its first vote.</summary>
    public IReadOnlyDictionary<string, long> FirstReadFrame => _firstReadFrame;

    internal double CenterX => Top.X + Top.Width / 2.0;

    internal double CenterY => Top.Y + Top.Height / 2.0;

    /// <summary>Adds a vote; returns true when this vote made a name the confirmed one.</summary>
    internal bool Vote(string name, long frame, double timeMs, int confirmReads, double confirmSpanMs)
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

        NameRead? best = null;
        foreach (var read in _reads)
            if (best is null || read.Count > best.Count)
                best = read;

        var rival = 0;
        foreach (var read in _reads)
            if (!ReferenceEquals(read, best) && !string.Equals(read.Name, best!.Name, StringComparison.Ordinal) && read.Count > rival)
                rival = read.Count;

        var trusted = best!.Count >= confirmReads && best.Count > rival && best.LastMs - best.FirstMs >= confirmSpanMs ? best.Name : null;
        if (string.Equals(trusted, ConfirmedName, StringComparison.Ordinal))
            return false;
        ConfirmedName = trusted; // a rival that catches up takes the confirmation back
        if (trusted is null)
            return false;
        ConfirmedFrame = frame;
        ConfirmedMs = timeMs;
        _confirmations.Add((trusted, frame, timeMs));
        return true;
    }
}

/// <summary>One name a track has read: how often, and when it was first and last read (ms of video time).</summary>
public sealed record NameRead(string Name, int Count, double FirstMs, double LastMs);

/// <summary>What one <see cref="NameplateTracker.Update"/> did.</summary>
/// <param name="Confirmed">Tracks that confirmed a name (again) in this frame.</param>
/// <param name="Closed">Tracks that expired in this frame (they are no longer in <see cref="NameplateTracker.Tracks"/>).</param>
public sealed record TrackerUpdate(IReadOnlyList<NameplateTrack> Confirmed, IReadOnlyList<NameplateTrack> Closed);

/// <summary>
/// Follows candidate pairs from frame to frame, keeps a sticky verdict per track and lets the reads of a nameplate vote on its
/// name. A port of <c>tracker_ref.py</c>; no OpenCV image work and no WPF, so it is testable with fake evaluations.
/// <list type="bullet">
/// <item>A candidate is matched to the nearest track of about the same size (closest first); otherwise it starts a new track and is
/// evaluated at once. Only strict candidates (both lines isolated) start tracks; a non-strict one can only continue a Nameplate track.</item>
/// <item>A <see cref="TrackVerdict.NotNameplate"/> track is not evaluated again until <see cref="NameplateTrackerSettings.RecheckMs"/> have
/// passed or its box size changed notably. Junk that stays on screen (HUD word pairs, signs) is read once, not every frame.</item>
/// <item>A <see cref="TrackVerdict.Nameplate"/> track is evaluated every frame until it has a confirmed name, then every
/// <see cref="NameplateTrackerSettings.ConfirmedRereadMs"/>. Each evaluation that reads a name surely enough is a vote. A name is confirmed
/// when it has <see cref="NameplateTrackerSettings.ConfirmReads"/> votes, more than any other name of the track, spread over at least
/// <see cref="NameplateTrackerSettings.ConfirmSpanMs"/>.</item>
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
    /// </summary>
    public TrackerUpdate Update(long frame, double timeMs, IReadOnlyList<TrackCandidate> candidates, Func<Rect, Rect, TrackEvaluation> evaluate)
    {
        var confirmedNow = new List<NameplateTrack>();
        var evaluations = 0;

        // match candidates and tracks, closest first
        var matches = new List<(double Distance, int Candidate, int Track)>();
        for (var c = 0; c < candidates.Count; c++)
        {
            for (var t = 0; t < _tracks.Count; t++)
            {
                if (!candidates[c].Strict && _tracks[t].Verdict != TrackVerdict.Nameplate)
                    continue;
                var d = SameObject(_tracks[t], candidates[c].Top);
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

        for (var c = 0; c < candidates.Count; c++)
        {
            var top = candidates[c].Top;
            var bottom = candidates[c].Bottom;
            if (trackOfCandidate.TryGetValue(c, out var index))
            {
                var track = _tracks[index];
                track.LastSeenMs = timeMs;
                var sizeChanged = SizeChanged(track, top);
                track.Top = top;
                track.Bottom = bottom;
                if (track.Verdict == TrackVerdict.NotNameplate)
                {
                    if (!(sizeChanged || timeMs - track.LastCheckedMs >= _settings.RecheckMs))
                        continue;
                }
                else if (track.ConfirmedName is not null && timeMs - track.LastReadMs < _settings.ConfirmedRereadMs && !sizeChanged)
                {
                    continue;
                }

                if (evaluations >= _settings.MaxEvaluationsPerFrame)
                    continue;
                evaluations++;
                Evaluate(track, frame, timeMs, evaluate, confirmedNow);
            }
            else
            {
                if (!candidates[c].Strict || evaluations >= _settings.MaxEvaluationsPerFrame)
                    continue;
                evaluations++;
                var track = new NameplateTrack(_nextId++, top, bottom, frame, timeMs);
                _tracks.Add(track);
                Evaluate(track, frame, timeMs, evaluate, confirmedNow);
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
        return new TrackerUpdate(confirmedNow, closed);
    }

    /// <summary>Closes every remaining track (end of a clip) and returns them.</summary>
    public IReadOnlyList<NameplateTrack> Finish()
    {
        var all = _tracks.ToList();
        _tracks.Clear();
        return all;
    }

    private void Evaluate(NameplateTrack track, long frame, double timeMs, Func<Rect, Rect, TrackEvaluation> evaluate, List<NameplateTrack> confirmedNow)
    {
        track.Evaluations++;
        track.LastCheckedMs = timeMs;
        track.CheckedWidth = track.Top.Width;
        track.CheckedHeight = track.Top.Height;
        var result = evaluate(track.Top, track.Bottom);
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
        if (result.Name is null)
            return;
        if (track.Vote(result.Name, frame, timeMs, _settings.ConfirmReads, _settings.ConfirmSpanMs))
            confirmedNow.Add(track);
    }

    /// <summary>Distance of the track's name line to <paramref name="top"/> when it can be the same object, null when it cannot.</summary>
    private double? SameObject(NameplateTrack track, Rect top)
    {
        var th = track.Top.Height;
        if (Math.Abs(top.Height - th) > Math.Max(2.0, 0.25 * th))
            return null;
        if (Math.Abs(top.Width - track.Top.Width) > _settings.WidthTolerance * track.Top.Width + 0.5 * th)
            return null;
        var cx = top.X + top.Width / 2.0;
        var cy = top.Y + top.Height / 2.0;
        var distance = Math.Sqrt((cx - track.CenterX) * (cx - track.CenterX) + (cy - track.CenterY) * (cy - track.CenterY));
        return distance <= _settings.MoveLines * th ? distance : null;
    }

    private bool SizeChanged(NameplateTrack track, Rect top) =>
        Math.Abs(top.Width - track.CheckedWidth) > _settings.SizeChange * track.CheckedWidth + 1
        || Math.Abs(top.Height - track.CheckedHeight) >= Math.Max(2.0, _settings.SizeChange * track.CheckedHeight);
}
