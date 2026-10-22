namespace Astra.Core.Cache;

/// <summary>
/// The cache timing rules the user can influence.
/// - Auto-refresh TTL: 30 days (default) or 7 days. An entry is stale when it is OLDER than the TTL.
/// The refresh interval can change while the app runs (Settings tab), so it is safe to read and write from any thread.
/// - Negative cache ("no such citizen"): remembered for 30 days, fixed.
/// </summary>
public sealed class CacheSettings
{
    public const int DefaultTtlDays = 30;

    /// <summary>How long a 404 is remembered. After this, the name is retried only if it is encountered again.</summary>
    public static readonly TimeSpan NotFoundRetention = TimeSpan.FromDays(30);

    private int _ttlDays;

    public CacheSettings(int ttlDays = DefaultTtlDays)
    {
        TtlDays = ttlDays;
    }

    /// <summary>The refresh interval in days: 7 or 30. Setting anything else throws.</summary>
    public int TtlDays
    {
        get => Volatile.Read(ref _ttlDays);
        set
        {
            if (value != 7 && value != 30)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The refresh interval must be 7 or 30 days.");
            Volatile.Write(ref _ttlDays, value);
        }
    }

    public TimeSpan Ttl => TimeSpan.FromDays(TtlDays);
}
