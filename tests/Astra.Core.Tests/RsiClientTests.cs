using System.Net;
using System.Text;
using System.Text.Json;
using Astra.Core.Rsi;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>An HTTP handler that answers from a script and remembers every request. Nothing touches the network.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Body, string? UserAgent)> Requests { get; } = new();

    public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } =
        (request, body) => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        string? userAgent = request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null;
        Requests.Add((request.Method, request.RequestUri!, body, userAgent));
        return Respond(request, body);
    }
}

public class RsiClientTests
{
    private const string PlayerPage = """
        <html><body><div class="profile-content">
        <div class="box-content org main"><div class="heading"><h3>Main organization</h3></div>
          <div class="inner"><a href="/orgs/HEKATE">Hekate</a></div></div>
        </div></body></html>
        """;

    private const string OrgPage = """
        <html><body><div id="organization"><h1>Jokers Gambit / J0K3R5</h1>
        <ul class="tags"><li>PMC</li></ul></div></body></html>
        """;

    private sealed class Setup
    {
        public FakeClock Clock { get; } = new();
        public FakeHandler Handler { get; } = new();
        public ListLog Log { get; } = new();
        public List<TimeSpan> Waits { get; } = new();
        public RsiClient Client { get; }

        public Setup()
        {
            var limiter = new RsiRateLimiter(Clock, (wait, ct) =>
            {
                Waits.Add(wait);
                Clock.Advance(wait);
                return Task.CompletedTask;
            });
            Client = new RsiClient(new HttpClient(Handler), limiter, Clock, Log);
        }
    }

    private static HttpResponseMessage Ok(string text, string mediaType = "text/html") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    private static string MembersJson(string fragment, int total) =>
        JsonSerializer.Serialize(new { success = 1, code = "OK", msg = "OK", data = new { html = fragment, totalrows = total } });

    private static string Fragment(params string[] handles) =>
        "<ul>" + string.Concat(handles.Select(h => $"<li class=\"member-item\"><a href=\"/citizens/{h}\">{h}</a></li>")) + "</ul>";

    [Fact]
    public async Task InvalidNames_AreRejectedBeforeAnyRequest()
    {
        var s = new Setup();

        var player = await s.Client.GetPlayerOrgsAsync("bad/name");
        var org = await s.Client.GetOrgInfoAsync("bad sid");
        var members = await s.Client.GetOrgMembersAsync(new string('X', 21));

        Assert.Equal(RsiStatus.Failed, player.Status);
        Assert.Equal("invalid name", player.Reason);
        Assert.Equal("invalid name", org.Reason);
        Assert.Equal("invalid name", members.Reason);
        Assert.Empty(s.Handler.Requests);
    }

    [Fact]
    public async Task Http404_IsNotFound_AndIsLoggedWithUrlTimeAndOutcome()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await s.Client.GetPlayerOrgsAsync("Nobody");

        Assert.Equal(RsiStatus.NotFound, result.Status);
        var entry = Assert.Single(s.Log.Entries);
        Assert.Contains("https://robertsspaceindustries.com/en/citizens/Nobody/organizations", entry.Message);
        Assert.Contains("not found", entry.Message);
        Assert.Equal(s.Clock.UtcNow, entry.Time);
    }

    [Fact]
    public async Task Http500_IsFailed_NotNotFound()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await s.Client.GetOrgInfoAsync("J0K3R5");

        Assert.Equal(RsiStatus.Failed, result.Status);
        Assert.Contains("500", result.Reason);
        Assert.Contains("error", Assert.Single(s.Log.Entries).Message);
    }

    [Fact]
    public async Task Timeout_IsFailed_NotNotFound()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => throw new TaskCanceledException("simulated timeout");

        var result = await s.Client.GetPlayerOrgsAsync("Nitebird");

        Assert.Equal(RsiStatus.Failed, result.Status);
        Assert.Contains("timeout", result.Reason);
    }

    [Fact]
    public async Task NetworkError_IsFailed()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => throw new HttpRequestException("connection refused");

        var result = await s.Client.GetPlayerOrgsAsync("Nitebird");

        Assert.Equal(RsiStatus.Failed, result.Status);
        Assert.Contains("connection refused", result.Reason);
    }

    [Fact]
    public async Task PageThatCannotBeUnderstood_IsFailed_NeverNotFound()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => Ok("<html><body>Down for maintenance</body></html>");

        var result = await s.Client.GetPlayerOrgsAsync("Nitebird");

        Assert.Equal(RsiStatus.Failed, result.Status);
    }

    [Fact]
    public async Task GoodPage_IsParsed_SendsDescriptiveUserAgent_AndLogsFound()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => Ok(PlayerPage);

        var result = await s.Client.GetPlayerOrgsAsync("Nitebird");

        Assert.True(result.IsFound, result.ToString());
        Assert.Equal("HEKATE", result.Data!.Orgs[0].Sid);
        var request = Assert.Single(s.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://robertsspaceindustries.com/en/citizens/Nitebird/organizations", request.Uri.AbsoluteUri);
        Assert.Equal("ASTRA/0.1 (Star Citizen fan overlay; +https://github.com/Niteb1rd/ASTRA)", request.UserAgent);
        Assert.Contains("found", Assert.Single(s.Log.Entries).Message);
    }

    [Fact]
    public async Task OrgPage_IsParsed()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => Ok(OrgPage);

        var result = await s.Client.GetOrgInfoAsync("J0K3R5");

        Assert.True(result.IsFound, result.ToString());
        Assert.Equal("Jokers Gambit", result.Data!.LongName);
        Assert.Equal("https://robertsspaceindustries.com/en/orgs/J0K3R5", Assert.Single(s.Handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task RequestsAreSpacedThreeSecondsApart_AndEveryRequestIsLogged()
    {
        var s = new Setup();
        var times = new List<DateTimeOffset>();
        s.Handler.Respond = (r, b) =>
        {
            times.Add(s.Clock.UtcNow);
            return Ok(PlayerPage);
        };

        await s.Client.GetPlayerOrgsAsync("One");
        await s.Client.GetPlayerOrgsAsync("Two");
        await s.Client.GetPlayerOrgsAsync("Three");

        Assert.Equal(3, times.Count);
        Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(3));
        Assert.True(times[2] - times[1] >= TimeSpan.FromSeconds(3));
        Assert.Equal(3, s.Log.Entries.Count);
    }

    [Fact]
    public async Task RedirectToAnotherHost_IsRefused_WithoutContactingIt()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example.com/steal");
            return response;
        };

        var result = await s.Client.GetPlayerOrgsAsync("Nitebird");

        Assert.Equal(RsiStatus.Failed, result.Status);
        Assert.Contains("robertsspaceindustries.com", result.Reason);
        Assert.Single(s.Handler.Requests);   // the second hop was never sent
        Assert.DoesNotContain(s.Handler.Requests, q => q.Uri.Host == "evil.example.com");
    }

    [Fact]
    public async Task RedirectWithinRsi_IsFollowed_AndRateLimitedLikeAnyRequest()
    {
        var s = new Setup();
        var hops = 0;
        s.Handler.Respond = (r, b) =>
        {
            hops++;
            if (hops == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                redirect.Headers.Location = new Uri("/en/citizens/Nitebird/organizations?x=1", UriKind.Relative);
                return redirect;
            }
            return Ok(PlayerPage);
        };

        var player = await s.Client.GetPlayerOrgsAsync("Nitebird");

        Assert.True(player.IsFound, player.ToString());
        Assert.Equal(2, s.Handler.Requests.Count);
        Assert.Equal("https://robertsspaceindustries.com/en/citizens/Nitebird/organizations?x=1", s.Handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(s.Waits));   // the second hop waited its turn
    }

    [Fact]
    public async Task Members_PagesThroughTheApi_WithTheDocumentedJsonBody()
    {
        var s = new Setup();
        s.Handler.Respond = (r, body) => body!.Contains("\"page\":2", StringComparison.Ordinal)
            ? Ok(MembersJson(Fragment("Carol"), 3), "application/json")
            : Ok(MembersJson(Fragment("Alice", "Bob"), 3), "application/json");

        var result = await s.Client.GetOrgMembersAsync("J0K3R5");

        Assert.True(result.IsFound, result.ToString());
        Assert.Equal("J0K3R5", result.Data!.Sid);
        Assert.Equal(new[] { "Alice", "Bob", "Carol" }, result.Data.Handles);

        Assert.Equal(2, s.Handler.Requests.Count);
        Assert.All(s.Handler.Requests, q =>
        {
            Assert.Equal(HttpMethod.Post, q.Method);
            Assert.Equal("https://robertsspaceindustries.com/api/orgs/getOrgMembers", q.Uri.AbsoluteUri);
        });
        Assert.Equal("{\"symbol\":\"J0K3R5\",\"search\":\"\",\"pagesize\":32,\"page\":1}", s.Handler.Requests[0].Body);
        Assert.Equal("{\"symbol\":\"J0K3R5\",\"search\":\"\",\"pagesize\":32,\"page\":2}", s.Handler.Requests[1].Body);
        Assert.Equal(2, s.Log.Entries.Count);   // each page request is logged
    }

    [Fact]
    public async Task Members_EmptyOrg_IsFoundWithNoHandles()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => Ok(MembersJson("<ul></ul>", 0), "application/json");

        var result = await s.Client.GetOrgMembersAsync("EMPTY");

        Assert.True(result.IsFound, result.ToString());
        Assert.Empty(result.Data!.Handles);
    }

    [Fact]
    public async Task Members_ReportedButNoneParsed_IsFailed_NotAnEmptyList()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => Ok(MembersJson("<div>layout we do not know</div>", 50), "application/json");

        var result = await s.Client.GetOrgMembersAsync("J0K3R5");

        Assert.Equal(RsiStatus.Failed, result.Status);
        Assert.Contains("layout", result.Reason);
    }

    [Fact]
    public async Task Members_Http404_IsFailed_BecauseOnlyThePageRequestsDecideNotFound()
    {
        var s = new Setup();
        s.Handler.Respond = (r, b) => new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await s.Client.GetOrgMembersAsync("J0K3R5");

        Assert.Equal(RsiStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Members_FailureOnALaterPage_FailsTheWholeRequest()
    {
        var s = new Setup();
        s.Handler.Respond = (r, body) => body!.Contains("\"page\":2", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Ok(MembersJson(Fragment("Alice", "Bob"), 5), "application/json");

        var result = await s.Client.GetOrgMembersAsync("J0K3R5");

        Assert.Equal(RsiStatus.Failed, result.Status);
        Assert.Contains("503", result.Reason);
    }
}
