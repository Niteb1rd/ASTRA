using Iris.Core.Detection.Letters;

namespace Iris.Core.Detection;

/// <summary>The verdict of <see cref="DistanceLineRule"/> on a line below a name.</summary>
public enum DistanceVerdict
{
    /// <summary>Not a distance line.</summary>
    Reject,

    /// <summary>A number followed by a unit ("9.0km", "835.5m").</summary>
    Full,

    /// <summary>A number, but the unit could not be recognized. Only good enough together with a very sure, long name.</summary>
    Partial,
}

/// <summary>
/// Decides from the model's restricted read of the line below a name whether it is the distance line of a nameplate. Tesseract is
/// not available to IRIS, and the letter model has two blind spots on this line: the game writes "9.0km" in lower case, which the
/// model never saw, so the unit comes out as K, M, "-", or a digit or two; and the decimal point is a tiny speck that is read as
/// "-" or "_" or dropped as noise. So the rule looks at the SHAPE of the read, not at its exact characters:
/// <list type="number">
/// <item>every piece of the read becomes a class: D (digit), P ("-" or "_", a dot or a lost unit), K, M; a gap wider than a
/// word gap before a piece is remembered;</item>
/// <item>a narrow first piece is a digit (a thin "1");</item>
/// <item>the line must start with a number: an optional P (".4"), 1 to 4 D, then a decimal part of one D, with or without a P in front;</item>
/// <item>the read must not have bent the model too far: the mean of (best log probability over all characters minus best
/// over the allowed ones) of the first 6 pieces must be at most <see cref="NameplateDetectorSettings.MaxDistanceGap"/>;</item>
/// <item>nothing after the number: <see cref="DistanceVerdict.Partial"/>; after it, K (+ optional P, M, K), M (+ optional P, M)
/// or P is the unit: <see cref="DistanceVerdict.Full"/> if at most <see cref="NameplateDetectorSettings.MaxGluedAfterUnit"/>
/// characters follow without a space (more means "209m/s (9.05km)": a speed line); any other character after the number: Partial.</item>
/// </list>
/// Port of <c>distance_status</c> in the Python reference. One instance reuses its work lists: not thread safe.
/// </summary>
public sealed class DistanceLineRule
{
    private readonly NameplateDetectorSettings _settings;
    private readonly List<char> _classes = new();
    private readonly List<bool> _gapBefore = new();

    public DistanceLineRule(NameplateDetectorSettings settings)
    {
        _settings = settings;
    }

    /// <summary>The characters the distance line is read with: digits, K, M, "-" and "_".</summary>
    public const string Alphabet = "0123456789KM-_";

    public DistanceVerdict Evaluate(PieceRead? read)
    {
        if (read is null || read.Pieces.Count == 0)
            return DistanceVerdict.Reject;
        return Evaluate(read.Pieces, read.CapHeight);
    }

    public DistanceVerdict Evaluate(IReadOnlyList<ReadPiece> pieces, int capHeight)
    {
        if (pieces.Count == 0)
            return DistanceVerdict.Reject;

        _classes.Clear();
        _gapBefore.Clear();
        var previousEnd = -1;
        foreach (var piece in pieces)
        {
            var first = true;
            foreach (var ch in piece.Text)
            {
                _classes.Add(ch is >= '0' and <= '9' ? 'D' : ch is '-' or '_' ? 'P' : ch);
                _gapBefore.Add(previousEnd >= 0 && first && piece.Start - previousEnd > _settings.WordGap * capHeight);
                first = false;
            }

            previousEnd = piece.End;
        }

        var firstPiece = pieces[0];
        if (firstPiece.End - firstPiece.Start < _settings.NarrowFirstPiece * capHeight)
        {
            _classes.RemoveRange(0, Math.Min(firstPiece.Text.Length, _classes.Count));
            _gapBefore.RemoveRange(0, Math.Min(firstPiece.Text.Length, _gapBefore.Count));
            _classes.Insert(0, 'D');
            _gapBefore.Insert(0, false);
        }

        if (_classes.Count == 0)
            return DistanceVerdict.Reject;

        var n = _classes.Count;
        var j = 0;
        if (j < n && _classes[j] == 'P' && j + 1 < n && _classes[j + 1] == 'D')
            j++;
        var digits = 0;
        while (j < n && _classes[j] == 'D' && digits < 4)
        {
            digits++;
            j++;
        }

        if (digits == 0)
            return DistanceVerdict.Reject;
        if (j < n && _classes[j] == 'P' && j + 1 < n && _classes[j + 1] == 'D')
            j += 2;
        else if (j < n && _classes[j] == 'D')
            j++;

        var count = Math.Min(6, pieces.Count);
        var gapSum = 0.0;
        for (var i = 0; i < count; i++)
            gapSum += (pieces[i].FreeScore - pieces[i].Score) / Math.Max(1, pieces[i].Text.Length);
        if (gapSum / count > _settings.MaxDistanceGap)
            return DistanceVerdict.Reject;

        if (j >= n)
            return DistanceVerdict.Partial;

        var c = _classes[j];
        if (c == 'K')
        {
            j++;
            if (j < n && (_classes[j] is 'P' or 'M' or 'K') && !_gapBefore[j])
                j++;
        }
        else if (c == 'M')
        {
            j++;
            if (j < n && (_classes[j] is 'P' or 'M') && !_gapBefore[j])
                j++;
        }
        else if (c == 'P')
        {
            j++;
        }
        else
        {
            return DistanceVerdict.Partial;
        }

        var glued = 0;
        while (j < n && !_gapBefore[j])
        {
            glued++;
            j++;
        }

        return glued <= _settings.MaxGluedAfterUnit ? DistanceVerdict.Full : DistanceVerdict.Reject;
    }
}
