namespace Astra.Core.Rsi;

/// <summary>
/// The one shared speed limit for RSI: at most one request every 3 seconds across the whole app.
/// Callers await <see cref="WaitForTurnAsync"/> right before sending; turns are handed out in the order
/// the calls arrive, so requests queue up first-come first-served.
///
/// How it works: each call reserves the next free time slot (slots are one interval apart) immediately,
/// then sleeps until its slot. The clock and the sleeping are injected so tests run instantly.
/// </summary>
public sealed class RsiRateLimiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;
    private DateTimeOffset _nextFreeSlot = DateTimeOffset.MinValue;

    /// <param name="clock">Source of "now".</param>
    /// <param name="delay">How to wait. Null means a real <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="interval">Minimum gap between requests. Null means 3 seconds.</param>
    public RsiRateLimiter(IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? interval = null)
    {
        _clock = clock;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Completes when it is this caller's turn to send one request.</summary>
    public async Task WaitForTurnAsync(CancellationToken ct = default)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _clock.UtcNow;
            var slot = _nextFreeSlot > now ? _nextFreeSlot : now;
            _nextFreeSlot = slot + _interval;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
            await _delay(wait, ct).ConfigureAwait(false);
    }
}
