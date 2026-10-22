using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Iris.Core.Rsi;

/// <summary>
/// Talks to robertsspaceindustries.com. Responsibilities, and nothing else:
/// - check names before building any URL (rejects with Failed("invalid name"));
/// - only ever contact robertsspaceindustries.com over https (redirects elsewhere are refused);
/// - wait for the shared <see cref="RsiRateLimiter"/> before every request (one request per second, app-wide) and tell it how RSI
///   answered, so it can slow down (HTTP 429, 5xx, timeout, network error) and speed up again (good answers);
/// - give up after 15 seconds;
/// - log every request: full URL, time, outcome and how long it took (send until the whole answer was read);
/// - turn the HTTP answer into Found / NotFound / Failed and hand page text to the pure parsers.
///
/// HTTP 404 is the only NotFound. Timeouts, 5xx, network errors and pages we cannot understand are Failed.
/// </summary>
public sealed class RsiClient : IRsiClient
{
    public const string UserAgent = "IRIS/0.1 (Star Citizen fan overlay; +https://github.com/Niteb1rd/IRIS)";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Members requested per API call.</summary>
    public const int MembersPageSize = 32;

    /// <summary>Safety stop for the members loop (400 pages of 32 is 12,800 members). Hitting it is reported as Failed.</summary>
    public const int MaxMemberPages = 400;

    private const int MaxRedirects = 3;

    private readonly HttpClient _http;
    private readonly RsiRateLimiter _limiter;
    private readonly IClock _clock;
    private readonly ILog _log;

    public RsiClient(HttpClient http, RsiRateLimiter limiter, IClock clock, ILog? log = null)
    {
        _http = http;
        _limiter = limiter;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>
    /// An HttpClient set up the way this class expects: redirects are left to <see cref="RsiClient"/>
    /// (so each hop is checked against the host rule) and the timeout is 15 seconds.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    public async Task<RsiResult<PlayerOrgs>> GetPlayerOrgsAsync(string handle, CancellationToken ct = default)
    {
        if (!RsiNames.IsValidHandle(handle))
            return Rejected<PlayerOrgs>("player page", handle);

        var url = RsiUrls.PlayerOrgs(handle);
        var response = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var result = Interpret(response, body => PlayerOrgsParser.Parse(body, handle));
        LogOutcome(HttpMethod.Get, response.Url, result.Status, result.Reason, null, response.Elapsed);
        NoteAnswer(response);
        return result;
    }

    public async Task<RsiResult<OrgInfo>> GetOrgInfoAsync(string sid, CancellationToken ct = default)
    {
        if (!RsiNames.IsValidSid(sid))
            return Rejected<OrgInfo>("org page", sid);

        var url = RsiUrls.OrgPage(sid);
        var response = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var result = Interpret(response, body => OrgPageParser.Parse(body, sid));
        LogOutcome(HttpMethod.Get, response.Url, result.Status, result.Reason, null, response.Elapsed);
        NoteAnswer(response);
        return result;
    }

    public async Task<RsiResult<OrgMembers>> GetOrgMembersAsync(string sid, CancellationToken ct = default)
    {
        if (!RsiNames.IsValidSid(sid))
            return Rejected<OrgMembers>("org members", sid);

        var handles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int entriesSoFar = 0;
        for (int page = 1; page <= MaxMemberPages; page++)
        {
            var body = JsonSerializer.Serialize(new { symbol = sid, search = "", pagesize = MembersPageSize, page });
            var response = await SendAsync(HttpMethod.Post, RsiUrls.OrgMembersApi, body, ct).ConfigureAwait(false);

            // A 404 from the API does not prove the org is unknown (the org page decides that), so it is a failure here.
            RsiResult<OrgMembersPage> result;
            if (response.StatusCode == 404)
                result = RsiResult<OrgMembersPage>.Failed("members API returned HTTP 404");
            else
                result = Interpret<OrgMembersPage>(response, OrgMembersParser.ParseResponse);
            LogOutcome(HttpMethod.Post, response.Url, result.Status, result.Reason, $"{sid} page {page}", response.Elapsed);
            NoteAnswer(response);

            if (result.Status != RsiStatus.Found)
                return RsiResult<OrgMembers>.Failed(result.Reason ?? "members request failed");

            var members = result.Data!;
            if (members.EntryCount == 0)
            {
                if (page == 1 && members.TotalCount is > 0)
                    return RsiResult<OrgMembers>.Failed(
                        $"members page 1 had no entries although RSI reports {members.TotalCount} members (page layout may have changed)");
                return RsiResult<OrgMembers>.Found(new OrgMembers(sid, handles));
            }

            foreach (var h in members.Handles)
                if (seen.Add(h))
                    handles.Add(h);

            // A full page always continues, so a stale or low totalrows can never cut the list short.
            // A short page ends the list once the reported total is reached (or when no total is given).
            entriesSoFar += members.EntryCount;
            bool shortPage = members.EntryCount < MembersPageSize;
            bool totalReached = members.TotalCount is not int total || entriesSoFar >= total;
            if (shortPage && totalReached)
                return RsiResult<OrgMembers>.Found(new OrgMembers(sid, handles));
        }

        return RsiResult<OrgMembers>.Failed($"gave up after {MaxMemberPages} member pages");
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>What came back from one (possibly redirected) HTTP exchange.</summary>
    /// <param name="Url">The last URL requested (after any redirects).</param>
    /// <param name="StatusCode">HTTP status, or null if no answer arrived.</param>
    /// <param name="Body">Response text (null if no answer arrived).</param>
    /// <param name="Error">Plain-words reason when no usable answer arrived (timeout, network error, blocked redirect).</param>
    /// <param name="Elapsed">How long the request took, from sending it until the whole answer was read (or it failed).</param>
    /// <param name="RetryAfter">The Retry-After RSI sent with an HTTP 429 or 503, if any.</param>
    /// <param name="NetworkProblem">True for a timeout or network error: RSI did not answer.</param>
    /// <param name="Turn">The limiter's generation when this request's turn came (see <see cref="RsiRateLimiter.WaitForTurnAsync"/>). Null when there was no request.</param>
    private sealed record Response(
        string Url, int? StatusCode, string? Body, string? Error,
        TimeSpan Elapsed = default, TimeSpan? RetryAfter = null, bool NetworkProblem = false, long? Turn = null);

    private async Task<Response> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        var current = url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) || !RsiUrls.IsAllowed(uri))
                return new Response(current, null, null, "blocked: only https://robertsspaceindustries.com may be contacted");

            var turn = await _limiter.WaitForTurnAsync(ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", "en");
                if (jsonBody is not null)
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using var httpResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                if (httpResponse.RequestMessage?.RequestUri is { } final && !RsiUrls.IsAllowed(final))
                    return new Response(final.AbsoluteUri, null, null, "blocked: only https://robertsspaceindustries.com may be contacted", stopwatch.Elapsed);
                int code = (int)httpResponse.StatusCode;

                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = httpResponse.Headers.Location;
                    if (method != HttpMethod.Get || location is null)
                        return new Response(uri.AbsoluteUri, code, null, $"unexpected redirect (HTTP {code})");

                    var next = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
                    Log($"{method} {uri.AbsoluteUri} -> redirect (HTTP {code}) to {next.AbsoluteUri} ({FormatDuration(stopwatch.Elapsed)})");
                    current = next.AbsoluteUri;
                    continue;
                }

                var retryAfter = code is 429 or 503 ? ReadRetryAfter(httpResponse.Headers.RetryAfter) : null;
                var text = await httpResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return new Response(uri.AbsoluteUri, code, text, null, stopwatch.Elapsed, retryAfter, Turn: turn);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log($"{method} {uri.AbsoluteUri} -> cancelled ({FormatDuration(stopwatch.Elapsed)})");
                throw;
            }
            catch (OperationCanceledException)
            {
                return new Response(uri.AbsoluteUri, null, null, $"timeout after {RequestTimeout.TotalSeconds:0} s", stopwatch.Elapsed, null, NetworkProblem: true);
            }
            catch (HttpRequestException ex)
            {
                return new Response(uri.AbsoluteUri, null, null, $"network error: {ex.Message}", stopwatch.Elapsed, null, NetworkProblem: true);
            }
            catch (IOException ex)
            {
                return new Response(uri.AbsoluteUri, null, null, $"network error: {ex.Message}", stopwatch.Elapsed, null, NetworkProblem: true);
            }
            catch (Exception ex)
            {
                return new Response(uri.AbsoluteUri, null, null, $"error: {ex.GetType().Name}: {ex.Message}", stopwatch.Elapsed);
            }
        }

        return new Response(current, null, null, $"too many redirects (more than {MaxRedirects})");
    }

    /// <summary>Maps an HTTP answer to Found / NotFound / Failed. Only HTTP 404 is NotFound.</summary>
    private static RsiResult<T> Interpret<T>(Response response, Func<string, RsiResult<T>> parse)
    {
        if (response.Error is not null)
            return RsiResult<T>.Failed(response.Error);

        int code = response.StatusCode ?? 0;
        if (code == 404)
            return RsiResult<T>.NotFound();
        if (code < 200 || code > 299)
            return RsiResult<T>.Failed($"HTTP {code}");

        try
        {
            return parse(response.Body ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RsiResult<T>.Failed($"could not read the page: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private RsiResult<T> Rejected<T>(string what, string name)
    {
        Log($"rejected {what} request: invalid name");
        return RsiResult<T>.Failed("invalid name");
    }

    private void LogOutcome(HttpMethod method, string url, RsiStatus status, string? reason, string? note, TimeSpan elapsed)
    {
        var outcome = status switch
        {
            RsiStatus.Found => "found",
            RsiStatus.NotFound => "not found (HTTP 404)",
            _ => $"error: {reason}",
        };
        Log($"{method} {url} -> {outcome}" + (note is null ? "" : $" [{note}]") + $" ({FormatDuration(elapsed)})");
    }

    /// <summary>"0.42 s". Invariant culture, so the log reads the same on every PC.</summary>
    public static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// Tells the rate limiter how RSI answered, and logs it when the gap between requests changes.
    /// Good answers are a page (HTTP 2xx) or a definite 404. Bad answers are HTTP 429, any 5xx, a timeout and a network error.
    /// Everything else (other 4xx, a refused address, an unexpected error) says nothing about how busy RSI is and changes nothing.
    /// </summary>
    private void NoteAnswer(Response response)
    {
        BackoffChange change;
        if (response.StatusCode is { } code)
        {
            if (code == 429 || code >= 500)
                change = _limiter.ReportThrottled(response.RetryAfter);
            else if ((code >= 200 && code <= 299) || code == 404)
                change = _limiter.ReportSuccess(response.Turn);
            else
                return;
        }
        else if (response.NetworkProblem)
        {
            change = _limiter.ReportThrottled();
        }
        else
        {
            return;
        }

        var gap = FormatSeconds(change.After);
        if (change.NextDelay > change.After)
            Log($"RSI is slowing us down: asked us to wait {FormatSeconds(change.NextDelay)} before the next request, then {gap} between requests");
        else if (change.After > change.Before)
            Log($"RSI is slowing us down: waiting {gap} between requests");
        else if (change.After < change.Before)
            Log($"RSI is answering normally again: waiting {gap} between requests");
    }

    private static string FormatSeconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";

    /// <summary>Retry-After as a wait from now: "120" (seconds) or a date. Null when absent, unreadable or already past.</summary>
    private TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null)
            return null;
        TimeSpan wait;
        if (header.Delta is { } delta)
            wait = delta;
        else if (header.Date is { } date)
            wait = date - _clock.UtcNow;
        else
            return null;
        return wait > TimeSpan.Zero ? wait : null;
    }

    private void Log(string message) => _log.Write(_clock.UtcNow, message);
}
