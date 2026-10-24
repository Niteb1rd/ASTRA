using Iris.Core.Detection.Letters;

namespace Iris.Core.Detection;

/// <summary>
/// The O/0 evidence of ONE track across frames (<see cref="NameplateTrackerSettings.O0MultiFrame"/>); a port of <c>o0_llr</c> and <c>o0_resolve</c> of <c>tracker_ref.py</c>
/// (training kit, README "O/0 evidence across frames"). The per-frame O/0 rule (<see cref="O0Rule"/>) measures every O or 0 glyph of a read and gives P(O); one frame is often not sure
/// (small glyphs) and the reads of a plate flip between O and 0 from frame to frame, which splits its votes over several spellings. Here the log odds of every frame are added up:
/// <list type="bullet">
/// <item>The evidence is kept per KEY, the read with every 0 turned into O (so all spellings of one name share it), and per position of that name: <c>total[pos] += llr(P)</c> for every
/// measured glyph whose character in the read is O or 0. <c>llr(P) = ln(P / (1 - P))</c>, clipped to plus or minus <see cref="LlrClip"/> (P of exactly 0 or 1 gives the clip).</item>
/// <item>A position is DECIDED when its total is at least <see cref="DecideLlr"/> = ln 19 away from 0 (odds of 19 to 1 = 95 %): a positive total is <c>O</c>, a negative one <c>0</c>.
/// The others keep the character of the read. The decision is a pure function of the totals (not latched, nothing is windowed; a vote against a decided position has to outweigh it).</item>
/// <item>The name returned is the read with its decided positions written, and it is what the track votes for.</item>
/// <item>At most <see cref="MaxKeys"/> keys per track; when a new key arrives at the limit, the oldest key is dropped.</item>
/// </list>
/// Not thread safe (a track belongs to one tracker).
/// </summary>
public sealed class O0EvidenceTable
{
    /// <summary>The most keys (folded names) one track keeps evidence for (<c>o0_max_keys</c>).</summary>
    public const int MaxKeys = 16;

    /// <summary>The log odds of one frame are clipped to plus or minus this (<c>o0_llr_clip</c>): at most one frame of a sure P decides alone (4 is above <see cref="DecideLlr"/>), no frame is worth more.</summary>
    public const double LlrClip = 4.0;

    /// <summary>The odds a position needs to be decided (<c>o0_decide_odds</c>): 19 to 1.</summary>
    public const double DecideOdds = 19.0;

    /// <summary>ln(<see cref="DecideOdds"/>), as the reference computes it (<c>math.log(19.0)</c>).</summary>
    public const double DecideLlr = 2.9444389791664403;

    private readonly Dictionary<string, double[]> _totals = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new(); // the keys, oldest first

    /// <summary>How many keys hold evidence.</summary>
    public int KeyCount => _totals.Count;

    /// <summary>The log odds of P(O) for one frame, clipped (<c>o0_llr</c>). P at or below 0 gives minus the clip, P at or above 1 plus the clip. A NaN gives 0 (no evidence; the reference has no such case).</summary>
    public static double Llr(double probabilityO)
    {
        if (double.IsNaN(probabilityO))
            return 0.0;
        if (probabilityO <= 0.0)
            return -LlrClip;
        if (probabilityO >= 1.0)
            return LlrClip;
        return Math.Max(-LlrClip, Math.Min(LlrClip, Math.Log(probabilityO / (1.0 - probabilityO))));
    }

    /// <summary>
    /// The totals of the key of <paramref name="name"/> (its folded form is looked up), one per character of the name; null when there is no evidence for it. A copy.
    /// </summary>
    public double[]? TotalsOf(string name) =>
        _totals.TryGetValue(LookAlikes.FoldO0(name), out var totals) ? (double[])totals.Clone() : null;

    /// <summary>
    /// Adds the evidence of one read and returns the name to vote for (<c>o0_resolve</c>). <paramref name="name"/> is the read as the reader put its O and 0 (the per-frame rule
    /// already applied); <paramref name="glyphs"/> are the glyphs the reader measured in it (null or empty: no new evidence, the totals of earlier frames still decide). A glyph whose
    /// index is outside the name or whose character in the name is no O and no 0 is ignored. The result has the same length; only O and 0 characters can differ from the read.
    /// </summary>
    public string Resolve(string name, IReadOnlyList<O0Glyph>? glyphs)
    {
        var key = LookAlikes.FoldO0(name);
        if (!_totals.TryGetValue(key, out var totals))
        {
            if (_totals.Count >= MaxKeys)
                _totals.Remove(_order.Dequeue()); // the oldest key goes
            totals = new double[name.Length];
            _totals[key] = totals;
            _order.Enqueue(key);
        }

        if (glyphs is not null)
        {
            foreach (var glyph in glyphs)
            {
                if (glyph.Index >= 0 && glyph.Index < name.Length && name[glyph.Index] is 'O' or '0')
                    totals[glyph.Index] += Llr(glyph.ProbabilityO);
            }
        }

        char[]? resolved = null;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is not ('O' or '0'))
                continue;
            var total = totals[i];
            if (Math.Abs(total) < DecideLlr)
                continue;
            var decided = total > 0 ? 'O' : '0';
            if (decided != name[i])
            {
                resolved ??= name.ToCharArray();
                resolved[i] = decided;
            }
        }

        return resolved is null ? name : new string(resolved);
    }
}
