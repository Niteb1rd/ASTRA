namespace Astra.Core;

/// <summary>
/// Where the current time comes from. Real code uses <see cref="SystemClock"/>;
/// tests use a fake so "30 days later" takes no time at all.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
