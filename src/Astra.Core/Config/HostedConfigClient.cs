using System.Security.Cryptography;
using Astra.Core.Rsi;

namespace Astra.Core.Config;

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
/// - redirects are never followed automatically: a redirect to the SAME site is followed by hand (at most 3 times),
///   a redirect to another site is refused with a message, so ASTRA never ends up talking to a site the user did not name;
/// - 15 seconds for the whole fetch; the answer may not be larger than <see cref="ConfigImporter.MaxFileBytes"/> (1 MB);
/// - every request is logged: full URL and outcome;
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
            Log($"hosted config: address refused ({problem})");
            return HostedConfigResult.Failed(url ?? string.Empty, problem);
        }

        var uri = new Uri(url.Trim());
        var startHost = uri.Host;

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

                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location.OriginalString);
                    Log($"GET {uri.AbsoluteUri} -> redirect (HTTP {code}) to {next.AbsoluteUri}");
                    if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        return Fail(uri, "The URL redirects to an address that is not https; only https:// is allowed.");
                    if (!string.Equals(next.Host, startHost, StringComparison.OrdinalIgnoreCase))
                        return Fail(uri, $"The URL redirects to another site ({next.Host}); use the final address instead.");
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
                Log($"GET {uri.AbsoluteUri} -> ok (HTTP {code}, {bytes.Length} bytes, sha256 {hash.Substring(0, 12)}...)");
                return HostedConfigResult.Ok(uri.AbsoluteUri, ConfigImporter.DecodeUtf8(bytes), hash);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log($"GET {uri.AbsoluteUri} -> cancelled");
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
        }

        return Fail(uri, $"The URL redirects too many times (more than {MaxRedirects}).");
    }

    /// <summary>SHA-256 of <paramref name="bytes"/> as lower-case hex.</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private HostedConfigResult Fail(Uri uri, string error)
    {
        Log($"GET {uri.AbsoluteUri} -> error: {error}");
        return HostedConfigResult.Failed(uri.AbsoluteUri, error);
    }

    private void Log(string message) => _log.Write(_clock.UtcNow, message);
}
