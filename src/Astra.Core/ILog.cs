namespace Astra.Core;

/// <summary>
/// A tiny logging hook. The session log file will implement this later;
/// until then <see cref="NullLog"/> throws everything away. No logging package is used on purpose.
/// </summary>
public interface ILog
{
    /// <summary>Records one line. <paramref name="utcTime"/> is when the thing happened.</summary>
    void Write(DateTimeOffset utcTime, string message);
}

/// <summary>A log that discards everything.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(DateTimeOffset utcTime, string message)
    {
    }
}
