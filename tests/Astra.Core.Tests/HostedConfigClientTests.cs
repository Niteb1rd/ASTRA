using System.Net;
using System.Text;
using Astra.Core.Config;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>An HTTP body whose length is not announced, so the size cap has to be enforced while reading.</summary>
internal sealed class UnknownLengthContent : HttpContent
{
    private readonly byte[] _bytes;

    public UnknownLengthContent(byte[] bytes)
    {
        _bytes = bytes;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(_bytes, 0, _bytes.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

public class HostedConfigClientTests
{
    private const string Url = "https://configs.example.com/squad/astra-config.toml";
    private const string Body = "format = \"sc-social-overlay\"\nformat_version = 1\n";

    private sealed class Setup
    {
        public FakeClock Clock { get; } = new();
        public FakeHandler Handler { get; } = new();
        public ListLog Log { get; } = new();
        public HostedConfigClient Client { get; }

        public Setup()
        {
            Client = new HostedConfigClient(new HttpClient(Handler), Clock, Log);
        }
    }

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task Success_ReturnsTextAndSha256()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "text/plain") };

        var result = await s.Client.FetchAsync(Url);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Body, result.Text);
        // SHA-256 of the exact bytes, lower-case hex
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Body))).ToLowerInvariant();
        Assert.Equal(expected, result.Hash);
        Assert.Equal(64, result.Hash!.Length);
        Assert.Equal(Url, result.Url);
        var request = Assert.Single(s.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Url, request.Uri.AbsoluteUri);
        Assert.StartsWith("ASTRA/", request.UserAgent);
    }

    [Fact]
    public async Task Success_DropsABom_ButHashesTheRawBytes()
    {
        var s = new Setup();
        var raw = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Body)).ToArray();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };

        var result = await s.Client.FetchAsync(Url);

        Assert.Equal(Body, result.Text);
        Assert.Equal(HostedConfigClient.Sha256Hex(raw), result.Hash);
    }

    [Theory]
    [InlineData("http://configs.example.com/a.toml")]
    [InlineData("ftp://configs.example.com/a.toml")]
    [InlineData("configs.example.com/a.toml")]
    [InlineData("")]
    [InlineData("https://user:pass@configs.example.com/a.toml")]
    public async Task NotAnHttpsAddress_IsRejected_WithoutAnyRequest(string url)
    {
        var s = new Setup();

        var result = await s.Client.FetchAsync(url);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(s.Handler.Requests);
        Assert.NotNull(HostedConfigUrl.Check(url));
    }

    [Fact]
    public void CheckUrl_AcceptsAPlainHttpsAddress()
    {
        Assert.Null(HostedConfigUrl.Check(Url));
        Assert.Null(HostedConfigUrl.Check("  " + Url + "  "));
    }

    [Fact]
    public async Task RedirectToAnotherSite_IsRefused()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => Redirect("https://evil.example.org/astra-config.toml");

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Equal("The URL redirects to another site (evil.example.org); use the final address instead.", result.Error);
        Assert.Single(s.Handler.Requests); // the other site was never contacted
    }

    [Fact]
    public async Task RedirectToHttp_IsRefused()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => Redirect("http://configs.example.com/a.toml");

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Single(s.Handler.Requests);
    }

    [Fact]
    public async Task RedirectOnTheSameSite_IsFollowed_AbsoluteAndRelative()
    {
        var s = new Setup();
        s.Handler.Respond = (request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/squad/astra-config.toml" => Redirect("https://CONFIGS.example.com/moved/config.toml", HttpStatusCode.MovedPermanently),
            "/moved/config.toml" => Redirect("/final/config.toml", HttpStatusCode.TemporaryRedirect),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) },
        };

        var result = await s.Client.FetchAsync(Url);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Body, result.Text);
        Assert.Equal("https://configs.example.com/final/config.toml", result.Url);
        Assert.Equal(3, s.Handler.Requests.Count);
    }

    [Fact]
    public async Task RedirectToAnotherPortOfTheSameHost_IsRefused()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => Redirect("https://configs.example.com:8443/a.toml");

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Contains("another site", result.Error);
        Assert.Contains("8443", result.Error);
        Assert.Single(s.Handler.Requests);
    }

    [Fact]
    public async Task RelativeRedirects_AreResolvedAgainstTheCurrentAddress()
    {
        var s = new Setup();
        s.Handler.Respond = (request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/squad/astra-config.toml" => Redirect("other.toml"),            // relative to /squad/
            "/squad/other.toml" => Redirect("/root.toml?x=1"),               // absolute path, same site
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) },
        };

        var result = await s.Client.FetchAsync(Url);

        Assert.True(result.Success, result.Error);
        Assert.Equal("https://configs.example.com/root.toml?x=1", result.Url);
    }

    [Fact]
    public async Task RefusedAddresses_AreLogged_WithoutAUserNameOrPassword()
    {
        var s = new Setup();

        await s.Client.FetchAsync("https://user:secret@configs.example.com/a.toml");
        await s.Client.FetchAsync("http://configs.example.com/plain.toml");
        await s.Client.FetchAsync("not an address at all");

        Assert.Empty(s.Handler.Requests);
        Assert.DoesNotContain(s.Log.Entries, e => e.Message.Contains("secret"));
        Assert.Contains(s.Log.Entries, e => e.Message.Contains("refused") && e.Message.Contains("configs.example.com/a.toml"));
        Assert.Contains(s.Log.Entries, e => e.Message.Contains("http://configs.example.com/plain.toml"));
        Assert.Contains(s.Log.Entries, e => e.Message.Contains("not an address at all"));
    }

    [Fact]
    public async Task RedirectToAnotherSite_IsLogged()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => Redirect("https://evil.example.org/astra-config.toml");

        await s.Client.FetchAsync(Url);

        Assert.Contains(s.Log.Entries, e => e.Message.Contains("evil.example.org") && e.Message.Contains("redirect"));
    }

    [Fact]
    public async Task MoreThanThreeRedirects_Fail()
    {
        var s = new Setup();
        var n = 0;
        s.Handler.Respond = (_, _) => Redirect("/hop" + (++n));

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Contains("too many", result.Error);
        Assert.Equal(4, s.Handler.Requests.Count); // the first request plus three followed redirects
    }

    [Fact]
    public async Task TooLarge_ByAnnouncedLength_IsRefused()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ConfigImporter.MaxFileBytes + 1]),
        };

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Contains("too large", result.Error);
    }

    [Fact]
    public async Task TooLarge_WithoutAnnouncedLength_IsRefusedWhileReading()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[ConfigImporter.MaxFileBytes + 1]),
        };

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Contains("too large", result.Error);
    }

    [Fact]
    public async Task ExactlyOneMegabyte_IsAccepted()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[ConfigImporter.MaxFileBytes]),
        };

        var result = await s.Client.FetchAsync(Url);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task NotFound_Is404Error_AndIsNotRemembered()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

        var first = await s.Client.FetchAsync(Url);
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
        var second = await s.Client.FetchAsync(Url);

        Assert.False(first.Success);
        Assert.Contains("404", first.Error);
        Assert.True(second.Success, second.Error); // a failure is never cached: the next try asks again
        Assert.Equal(2, s.Handler.Requests.Count);
    }

    [Fact]
    public async Task ServerError_IsAnError()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task NetworkError_IsAnErrorNotAnException()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => throw new HttpRequestException("no route");

        var result = await s.Client.FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Contains("no route", result.Error);
    }

    [Fact]
    public async Task EveryRequestUrlIsLogged_WithItsOutcome()
    {
        var s = new Setup();
        s.Handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/squad/astra-config.toml"
            ? Redirect("/final.toml")
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        await s.Client.FetchAsync(Url);

        Assert.Contains(s.Log.Entries, e => e.Message.Contains(Url) && e.Message.Contains("redirect"));
        Assert.Contains(s.Log.Entries, e => e.Message.Contains("https://configs.example.com/final.toml") && e.Message.Contains("404"));
    }

    [Fact]
    public async Task Cancelling_ThrowsOperationCanceled()
    {
        var s = new Setup();
        s.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Client.FetchAsync(Url, cts.Token));
    }

    [Fact]
    public void CreateHttpClient_TimesOutAfterFifteenSeconds()
    {
        using var http = HostedConfigClient.CreateHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(15), http.Timeout);
    }
}
