// Saves real RSI pages into tests/Astra.Core.Tests/Fixtures/rsi/ so the parser tests can check
// ASTRA's HTML/JSON assumptions against what RSI really sends.
//
//   dotnet run --project tools\Astra.CaptureFixtures
//
// Politeness: it only contacts robertsspaceindustries.com, one request every 3 seconds, about 6 requests in total.
// The saved files are copies of third-party pages: they are git-ignored and must not be committed.

using System.Text;

const string Host = "robertsspaceindustries.com";
const string UserAgent = "ASTRA/0.1 (Star Citizen fan overlay; +https://github.com/Niteb1rd/ASTRA)";
var pause = TimeSpan.FromSeconds(3);

var folder = FindOutputFolder();
if (folder is null)
{
    Console.Error.WriteLine("Could not find tests\\Astra.Core.Tests. Run this from the ASTRA repository folder:");
    Console.Error.WriteLine("  dotnet run --project tools\\Astra.CaptureFixtures");
    return 1;
}
string outputFolder = folder;
Directory.CreateDirectory(outputFolder);
Console.WriteLine($"Saving into {outputFolder}");

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
bool firstRequest = true;
int problems = 0;

// player pages
await Capture("player-Nitebird.html", $"https://{Host}/en/citizens/Nitebird/organizations");
await Capture("player-Bqnzai.html", $"https://{Host}/en/citizens/Bqnzai/organizations");
await Capture("player-Love2Hate.html", $"https://{Host}/en/citizens/Love2Hate/organizations");

// a handle that surely does not exist: only the status code is recorded (expected 404)
await Capture("player-nonexistent.status.txt", $"https://{Host}/en/citizens/AstraNoSuchCitizen7x3q9/organizations", statusOnly: true);

// org page and members API, page 1
await Capture("org-J0K3R5.html", $"https://{Host}/en/orgs/J0K3R5");
await Capture(
    "members-J0K3R5-page1.json",
    $"https://{Host}/api/orgs/getOrgMembers",
    jsonBody: "{\"symbol\":\"J0K3R5\",\"search\":\"\",\"pagesize\":32,\"page\":1}");

Console.WriteLine();
if (problems == 0)
{
    Console.WriteLine("Done. Now run:  dotnet test tests\\Astra.Core.Tests");
    return 0;
}
Console.WriteLine($"Done, but {problems} request(s) did not work (see above). The matching tests will stay skipped.");
return 1;

async Task Capture(string fileName, string url, string? jsonBody = null, bool statusOnly = false)
{
    if (!firstRequest)
        await Task.Delay(pause);
    firstRequest = false;

    var uri = new Uri(url);
    if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("This tool only contacts https://" + Host);

    var method = jsonBody is null ? HttpMethod.Get : HttpMethod.Post;
    Console.Write($"{method} {url} ... ");
    try
    {
        // Redirects are followed by hand, only within https://robertsspaceindustries.com,
        // with the same pause before each hop. Nothing else is ever contacted.
        HttpResponseMessage response = await Send(method, uri, jsonBody);
        for (int hop = 0; (int)response.StatusCode is >= 300 and < 400; hop++)
        {
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || method != HttpMethod.Get || hop >= 3)
            {
                Console.WriteLine("unexpected redirect; not saved");
                problems++;
                return;
            }
            var next = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
            if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, Host, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"redirect to {next.Host} refused; not saved");
                problems++;
                return;
            }
            Console.Write($"redirect -> {next.AbsoluteUri} ... ");
            await Task.Delay(pause);
            uri = next;
            response = await Send(method, uri, jsonBody);
        }
        using var _ = response;

        int code = (int)response.StatusCode;
        var path = Path.Combine(outputFolder, fileName);
        if (statusOnly)
        {
            File.WriteAllText(path, code.ToString());
            Console.WriteLine($"HTTP {code} -> {fileName}");
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"HTTP {code}; not saved");
            problems++;
            return;
        }

        var text = await response.Content.ReadAsStringAsync();
        File.WriteAllText(path, text, new UTF8Encoding(false));
        Console.WriteLine($"HTTP {code} -> {fileName} ({text.Length:N0} characters)");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"failed: {ex.Message}");
        problems++;
    }
}

async Task<HttpResponseMessage> Send(HttpMethod method, Uri uri, string? jsonBody)
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
    return await http.SendAsync(request);
}

static string? FindOutputFolder()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (DirectoryInfo? dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var tests = Path.Combine(dir.FullName, "tests", "Astra.Core.Tests");
            if (Directory.Exists(tests))
                return Path.Combine(tests, "Fixtures", "rsi");
        }
    }
    return null;
}
