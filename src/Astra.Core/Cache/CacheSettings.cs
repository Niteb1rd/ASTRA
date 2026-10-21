namespace Astra.Core.Cache;

/// <summary>
/// The cache timing rules the user can influence.
/// - Auto-refresh TTL: 30 days (default) or 7 days. An entry is stale when it is OLDER than the TTL.
/// - Negative cache ("no such citizen"): remembered for 30 days, fixed.
/// </summary>
public sealed class CacheSettings
{
    public const int DefaultTtlDays = 30;

    /// <summary>How long a 404 is remembered. After this, the name is retried only if it is encountered again.</summary>
    public static readonly TimeSpan NotFoundRetention = TimeSpan.FromDays(30);

    public CacheSettings(int ttlDays = DefaultTtlDays)
    {
        if (ttlDays != 7 && ttlDays != 30)
            throw new ArgumentOutOfRangeException(nameof(ttlDays), ttlDays, "The refresh interval must be 7 or 30 days.");
        TtlDays = ttlDays;
    }

    public int TtlDays { get; }

    public TimeSpan Ttl => TimeSpan.FromDays(TtlDays);
}
