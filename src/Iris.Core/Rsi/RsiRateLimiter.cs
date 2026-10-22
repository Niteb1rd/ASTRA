namespace Iris.Core.Rsi;

/// <summary>What <see cref="RsiRateLimiter.ReportSuccess"/> and <see cref="RsiRateLimiter.ReportThrottled"/> changed.</summary>
/// <param name="Before">The gap between requests before the report.</param>
/// <param name="After">The gap between requests after the report.</param>
/// <param name="NextDelay">How long the very next request will wait (after a throttled answer): the gap, or RSI's Retry-After if that is longer. Zero after a success.</param>
public readonly record struct BackoffChange(TimeSpan Before, TimeSpan After, TimeSpan NextDelay)
{
    public bool Changed => Before != After;
}

/// <summary>
/// The one shared speed limit for RSI: normally at most one request per second across the whole app.
/// Callers await <see cref="WaitForTurnAsync"/> right before sending. Slots are reserved in the order the calls arrive, so requests
/// normally go out first-come first-served. That order is NOT guaranteed after a bad answer: the callers that are already waiting
/// give their slot back when they wake up and queue again (each still waits its full time), so they may swap places.
///
/// How it works: each call reserves the next free time slot (slots are one gap apart) immediately,
/// then sleeps until its slot. The clock and the sleeping are injected so tests run instantly.
///
/// Backoff: when RSI answers with HTTP 429, any 5xx, or does not answer (timeout, network error) the caller reports it with
/// <see cref="ReportThrottled"/>. The gap between requests then doubles (1, 2, 4, 8, 16, 30 s; 30 s is the cap) and the next request
/// waits that long, counted from the moment the bad answer arrived. A Retry-After from RSI makes the next wait longer if it asks for
/// more (cap 120 s). Every good answer (<see cref="ReportSuccess"/>) halves the gap again, back down to the normal 1 s.
/// A good answer to a request that was sent BEFORE the latest bad answer says nothing about how RSI is doing now, so it is ignored:
/// every bad answer starts a new "generation", <see cref="WaitForTurnAsync"/> returns the generation a request was sent in, and
/// <see cref="ReportSuccess"/> only counts when that is still the current one.
/// Requests that were already waiting for their slot when the bad answer arrived wait the new, longer time as well.
/// </summary>
public sealed class RsiRateLimiter
{
    /// <summary>The normal gap between two requests.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>The largest gap backoff ever grows to.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>The longest single wait a Retry-After can cause.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(120);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;

    // The gap between two requests right now (the interval, or more while RSI is slowing us down).
    private TimeSpan _gap;

    // Start of the most recently reserved slot. The next slot is at least one gap after it.
    private DateTimeOffset _lastSlot = DateTimeOffset.MinValue;

    // No request may be sent before this time (set by a throttled answer).
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    // Bumped by every throttled answer. A request remembers the value from the moment its turn came.
    private long _generation;

    /// <param name="clock">Source of "now".</param>
    /// <param name="delay">How to wait. Null means a real <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. A fake must move the fake clock forward by the wait.</param>
    /// <param name="interval">Normal gap between requests. Null means 1 second.</param>
    public RsiRateLimiter(IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? interval = null)
    {
        _clock = clock;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _interval = interval ?? DefaultInterval;
        _gap = _interval;
    }

    /// <summary>The gap between requests right now: the normal interval, or more while backoff is active.</summary>
    public TimeSpan CurrentGap
    {
        get
        {
            lock (_gate)
                return _gap;
        }
    }

    /// <summary>
    /// RSI answered normally (a page, or a definite 404): halves the gap, down to the normal interval.
    /// </summary>
    /// <param name="turn">
    /// The value <see cref="WaitForTurnAsync"/> returned for the request that got this answer. If a bad answer was reported after that
    /// turn came, the good answer is ignored (nothing changes). Null always counts.
    /// </param>
    public BackoffChange ReportSuccess(long? turn = null)
    {
        lock (_gate)
        {
            var before = _gap;
            if (turn is { } sentIn && sentIn != _generation)
                return new BackoffChange(before, before, TimeSpan.Zero);

            var halved = TimeSpan.FromTicks(_gap.Ticks / 2);
            _gap = halved < _interval ? _interval : halved;
            return new BackoffChange(before, _gap, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// RSI said "too many requests", failed (5xx) or did not answer: doubles the gap (cap 30 s) and holds back the next request.
    /// </summary>
    /// <param name="retryAfter">The Retry-After RSI sent with a 429 or 503, if any. The next wait is the longer of this and the new gap, at most 120 s.</param>
    public BackoffChange ReportThrottled(TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var before = _gap;
            _generation++;
            var doubled = TimeSpan.FromTicks(_gap.Ticks * 2);
            _gap = doubled > MaxBackoff ? MaxBackoff : doubled;
            if (_gap < _interval)
                _gap = _interval;

            var wait = _gap;
            if (retryAfter is { } asked && asked > wait)
                wait = asked > MaxRetryAfter ? MaxRetryAfter : asked;

            var until = _clock.UtcNow + wait;
            if (until > _notBefore)
                _notBefore = until;
            return new BackoffChange(before, _gap, wait);
        }
    }

    /// <summary>
    /// Completes when it is this caller's turn to send one request. The result is the generation at that moment: pass it to
    /// <see cref="ReportSuccess"/> together with the answer to this request.
    /// </summary>
    public async Task<long> WaitForTurnAsync(CancellationToken ct = default)
    {
        Reservation? previous = null;
        while (true)
        {
            Reservation reservation;
            long generation;
            lock (_gate)
            {
                var now = _clock.UtcNow;
                generation = _generation;

                // Back after a wait: if a bad answer arrived meanwhile and pushed the allowed time further out, queue up again behind it.
                if (previous is not null && _notBefore <= now)
                    return generation;

                // Our old slot is no use any more; hand it back first so it does not hold up our own new slot.
                if (previous is { } old)
                    Release(old);

                reservation = Reserve(now);
            }
            previous = reservation;

            if (reservation.Wait <= TimeSpan.Zero)
                return generation; // the slot is now, and a slot is never earlier than the allowed time

            try
            {
                await _delay(reservation.Wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                    Release(reservation);
                throw;
            }
        }
    }

    // Caller holds _gate.
    private Reservation Reserve(DateTimeOffset now)
    {
        var earliest = _lastSlot + _gap;
        if (_notBefore > earliest)
            earliest = _notBefore;
        var slot = earliest > now ? earliest : now;

        var reservation = new Reservation(slot, _lastSlot, slot - now);
        _lastSlot = slot;
        return reservation;
    }

    // Caller holds _gate. Gives the slot back, but only if nobody reserved after us (otherwise their slot would move up too far).
    private void Release(Reservation reservation)
    {
        if (_lastSlot == reservation.Slot)
            _lastSlot = reservation.PreviousSlot;
    }

    private readonly record struct Reservation(DateTimeOffset Slot, DateTimeOffset PreviousSlot, TimeSpan Wait);
}
