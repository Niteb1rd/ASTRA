using System.Security.Cryptography;
using Iris.Core.Rsi;

namespace Iris.Core.Config;

/// <summary>The answer of <see cref="HostedConfigClient.FetchAsync"/>: the file text and its hash, or the reason it failed.</summary>
public sealed class HostedConfigResult
{
    private HostedConfigResult()
    {
    }

    public bool Success { get; private init; }

    /// <summary>Plain-words reason. Only set when the fetch failed.</summary>
    public string? Error { get; private init; }

    /// <summary>The address that finally answered (after any same-site redirects).</summary>
    public string Url { get; private init; } = string.Empty;

    /// <summary>The file as text (UTF-8, byte-order mark removed). Only set on success. Not parsed or checked here.</summary>
    public string? Text { get; private init; }

    /// <summary>SHA-256 of the downloaded bytes as lower-case hex (64 characters). Only set on success.</summary>
    public string? Hash { get; private init; }

    internal static HostedConfigResult Ok(string url, string text, string hash) =>
        new() { Success = true, Url = url, Text = text, Hash = hash };

    internal static HostedConfigResult Failed(string url, string error) =>
        new() { Success = false, Url = url, Error = error };
}

/// <summary>
/// Downloads the user's own hosted config file (one GET, nothing else). Rules:
/// - https only, and no user name or password in the address;
/// - redirects are never followed automatically: a redirect to the SAME site (same host and port) is followed by hand (at most 3 times),
///   a redirect to another site is refused with a message, so IRIS never ends up talking to a site the user did not name;
/// - 15 seconds for the whole fetch; the answer may not be larger than <see cref="ConfigImporter.MaxFileBytes"/> (1 MB);
/// - every request is logged: URL (without any user name or password) and outcome; refused addresses are logged too;
/// - nothing is cached, so a failed fetch never sticks.
/// The text is NOT parsed here; the caller hands it to <see cref="SocialConfigToml.Read"/>.
/// </summary>
public sealed class HostedConfigClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const int MaxRedirects = 3;

    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly ILog _log;

    public HostedConfigClient(HttpClient http, IClock clock, ILog? log = null)
    {
        _http = http;
        _clock = clock;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>An HttpClient set up the way this class expects: no automatic redirects, 15 second timeout.</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    /// <summary>Downloads the file. Never throws for network trouble; throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> is cancelled.</summary>
    public async Task<HostedConfigResult> FetchAsync(string url, CancellationToken ct = default)
    {
        var problem = HostedConfigUrl.Check(url);
        if (problem is not null)
        {
            Log($"hosted config: address refused ({problem}): {Display(url ?? string.Empty)}");
            return HostedConfigResult.Failed(url ?? string.Empty, problem);
        }

        var uri = new Uri(url.Trim());
        var startHost = uri.Host;
        var startPort = uri.Port;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", RsiClient.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/toml, text/plain, */*");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                int code = (int)response.StatusCode;

                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return Fail(uri, "The URL redirects without saying where to.");

                    // A relative Location ("/final.toml") is resolved against the current address. On Linux a path starting with "/"
                    // parses as an absolute file:// address, so file addresses are treated as relative too.
                    var next = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
                    Log($"GET {Display(uri)} -> redirect (HTTP {code}) to {Display(next)}");
                    if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        return Fail(uri, "The URL redirects to an address that is not https; only https:// is allowed.");
                    if (!string.Equals(next.Host, startHost, StringComparison.OrdinalIgnoreCase) || next.Port != startPort)
                        return Fail(uri, $"The URL redirects to another site ({next.Authority}); use the final address instead.");
                    uri = next;
                    continue;
                }

                if (code == 404)
                    return Fail(uri, "The URL returned HTTP 404 (not found). Check the address.");
                if (code < 200 || code > 299)
                    return Fail(uri, $"The server answered HTTP {code}.");

                var announced = response.Content.Headers.ContentLength;
                if (announced is > ConfigImporter.MaxFileBytes)
                    return Fail(uri, "The file is too large (over 1 MB). A config file is normally a few KB.");

                using var body = new MemoryStream();
                await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                {
                    var chunk = new byte[16 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        body.Write(chunk, 0, read);
                        if (body.Length > ConfigImporter.MaxFileBytes)
                            return Fail(uri, "The file is too large (over 1 MB). A config file is normally a few KB.");
                    }
                }

                var bytes = body.ToArray();
                var hash = Sha256Hex(bytes);
                Log($"GET {Display(uri)} -> ok (HTTP {code}, {bytes.Length} bytes, sha256 {hash.Substring(0, 12)}...)");
                return HostedConfigResult.Ok(Display(uri), ConfigImporter.DecodeUtf8(bytes), hash);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log($"GET {Display(uri)} -> cancelled");
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fail(uri, $"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds.");
            }
            catch (HttpRequestException ex)
            {
                return Fail(uri, "Network error: " + ex.Message);
            }
            catch (IOException ex)
            {
                return Fail(uri, "Network error: " + ex.Message);
            }
            catch (UriFormatException)
            {
                return Fail(uri, "The URL redirects to an address that is not valid.");
            }
        }

        return Fail(uri, $"The URL redirects too many times (more than {MaxRedirects}).");
    }

    /// <summary>SHA-256 of <paramref name="bytes"/> as lower-case hex.</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private HostedConfigResult Fail(Uri uri, string error)
    {
        Log($"GET {Display(uri)} -> error: {error}");
        return HostedConfigResult.Failed(Display(uri), error);
    }

    /// <summary>The address for the log and for results: any user name or password is left out. Text that is not an address is returned as it is.</summary>
    private static string Display(string text)
    {
        try
        {
            if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
                return Display(parsed);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
        }
        return text;
    }

    private static string Display(Uri uri)
    {
        if (uri.UserInfo.Length == 0)
            return uri.AbsoluteUri;
        try
        {
            var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            return builder.Uri.AbsoluteUri;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            return uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", string.Empty) + uri.PathAndQuery;
        }
    }

    private void Log(string message) => _log.Write(_clock.UtcNow, message);
}
