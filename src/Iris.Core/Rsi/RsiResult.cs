namespace Iris.Core.Rsi;

/// <summary>The three possible outcomes of an RSI page request.</summary>
public enum RsiStatus
{
    /// <summary>The page was fetched and understood.</summary>
    Found,

    /// <summary>RSI answered with a definite HTTP 404: no such citizen / org. The only outcome that may be negative-cached.</summary>
    NotFound,

    /// <summary>Anything else: timeout, HTTP 5xx, network error, a page we could not understand, a rejected name.</summary>
    Failed,
}

/// <summary>
/// The result of asking RSI for something. Outcomes are values, not exceptions,
/// so callers must decide what to do with each case.
/// </summary>
public sealed class RsiResult<T>
{
    private RsiResult(RsiStatus status, T? data, string? reason)
    {
        Status = status;
        Data = data;
        Reason = reason;
    }

    public RsiStatus Status { get; }

    /// <summary>The parsed data. Only set when <see cref="Status"/> is Found.</summary>
    public T? Data { get; }

    /// <summary>Plain-words reason. Only set when <see cref="Status"/> is Failed.</summary>
    public string? Reason { get; }

    public bool IsFound => Status == RsiStatus.Found;

    public static RsiResult<T> Found(T data) => new(RsiStatus.Found, data, null);

    public static RsiResult<T> NotFound() => new(RsiStatus.NotFound, default, null);

    public static RsiResult<T> Failed(string reason) => new(RsiStatus.Failed, default, reason);

    public override string ToString() => Status switch
    {
        RsiStatus.Found => "Found",
        RsiStatus.NotFound => "NotFound",
        _ => $"Failed({Reason})",
    };
}
