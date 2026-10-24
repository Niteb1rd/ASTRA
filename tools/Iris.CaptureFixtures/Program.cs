// Saves real RSI pages into tests/Iris.Core.Tests/Fixtures/rsi/ so the parser tests can check
// IRIS's HTML/JSON assumptions against what RSI really sends.
//
// Default set (no options), used by the fixture tests:
//   dotnet run --project tools\Iris.CaptureFixtures
//
// Extra pages for any players and orgs you name:
//   dotnet run --project tools\Iris.CaptureFixtures -- --orgs-of Nitebird --players SomeName,AnotherName
//
// Options (combine freely; names separated by commas):
//   --players A,B,C      save each player's organizations page (works for unknown names too: the HTTP status is saved)
//   --orgs A,B           save each org's page and its member list
//   --orgs-of HANDLE     read HANDLE's organizations page, then do --orgs for every visible org on it
//   --member-pages N     member-list pages to save per org (32 members each; default 1)
//
// Politeness: it only contacts robertsspaceindustries.com, one request every 3 seconds.
// The saved files are copies of third-party pages: they are git-ignored and must not be committed.

using System.Text;
using Iris.Core.Config;
using Iris.Core.Rsi;

const string Host = "robertsspaceindustries.com";
const string UserAgent = "IRIS/0.1 (Star Citizen fan overlay; +https://github.com/Niteb1rd/IRIS)";
var pause = TimeSpan.FromSeconds(3);

// ------------------------------------------------------------------ options
var players = new List<string>();
var orgs = new List<string>();
var orgsOf = new List<string>();
int memberPages = 1;
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i].ToLowerInvariant())
    {
        case "--players": players.AddRange(SplitNames(Next())); break;
        case "--orgs": orgs.AddRange(SplitNames(Next())); break;
        case "--orgs-of": orgsOf.AddRange(SplitNames(Next())); break;
        case "--member-pages":
            if (!int.TryParse(Next(), out memberPages) || memberPages < 1 || memberPages > 400)
            {
                Console.Error.WriteLine("--member-pages must be a number from 1 to 400");
                return 1;
            }
            break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}. See the comment at the top of Program.cs for usage.");
            return 1;
    }
}
bool defaultSet = players.Count == 0 && orgs.Count == 0 && orgsOf.Count == 0;

// names must be valid before any URL is built
foreach (var p in players.Concat(orgsOf))
    if (!RsiNames.IsValidHandle(p)) { Console.Error.WriteLine($"\"{p}\" is not a valid handle. {RowValidator.InvalidHandleMessage}."); return 1; }
foreach (var o in orgs)
    if (!RsiNames.IsValidSid(o)) { Console.Error.WriteLine($"\"{o}\" is not a valid org SID (letters, digits, - and _ only)."); return 1; }

var folder = FindOutputFolder();
if (folder is null)
{
    Console.Error.WriteLine("Could not find tests\\Iris.Core.Tests. Run this from the IRIS repository folder:");
    Console.Error.WriteLine("  dotnet run --project tools\\Iris.CaptureFixtures");
    return 1;
}
string outputFolder = folder;
Directory.CreateDirectory(outputFolder);
Console.WriteLine($"Saving into {outputFolder}");

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
bool firstRequest = true;
int problems = 0;
var summary = new List<string>();

if (defaultSet)
{
    // the set the fixture tests expect
    await Capture("player-Nitebird.html", $"https://{Host}/en/citizens/Nitebird/organizations");
    await Capture("player-Bqnzai.html", $"https://{Host}/en/citizens/Bqnzai/organizations");
    await Capture("player-Love2Hate.html", $"https://{Host}/en/citizens/Love2Hate/organizations");
    await Capture("player-nonexistent.status.txt", $"https://{Host}/en/citizens/IrisNoSuchCitizen7x3q9/organizations", statusOnly: true);
    await Capture("org-J0K3R5.html", $"https://{Host}/en/orgs/J0K3R5");
    await Capture("members-J0K3R5-page1.json", $"https://{Host}/api/orgs/getOrgMembers", jsonBody: MembersBody("J0K3R5", 1));
}
else
{
    foreach (var handle in players)
        await CapturePlayer(handle);

    foreach (var handle in orgsOf)
    {
        var page = await CapturePlayer(handle);
        if (page is null)
            continue;
        var visible = page.Orgs.Where(o => o.Sid is not null).Select(o => o.Sid!).ToList();
        Console.WriteLine($"  {handle}'s visible orgs: {(visible.Count == 0 ? "none" : string.Join(", ", visible))}");
        foreach (var sid in visible)
            if (!orgs.Contains(sid, StringComparer.OrdinalIgnoreCase))
                orgs.Add(sid);
    }

    foreach (var sid in orgs)
        await CaptureOrg(sid);

    Console.WriteLine();
    Console.WriteLine("What each page shows (read with IRIS's own parsers):");
    foreach (var line in summary)
        Console.WriteLine("  " + line);
}

Console.WriteLine();
if (problems == 0)
{
    Console.WriteLine("Done. Now run:  dotnet test tests\\Iris.Core.Tests");
    return 0;
}
Console.WriteLine($"Done, but {problems} request(s) did not work (see above).");
return 1;

// ------------------------------------------------------------------ players and orgs

async Task<PlayerOrgs?> CapturePlayer(string handle)
{
    var (code, text) = await Capture($"player-{handle}.html", $"https://{Host}/en/citizens/{handle}/organizations", saveStatus: $"player-{handle}.status.txt");
    if (code == 404)
    {
        summary.Add($"player {handle}: not found (HTTP 404)");
        return null;
    }
    if (text is null)
    {
        summary.Add($"player {handle}: no page (HTTP {code?.ToString() ?? "none"})");
        return null;
    }
    var parsed = PlayerOrgsParser.Parse(text, handle);
    if (!parsed.IsFound)
    {
        summary.Add($"player {handle}: page saved but not understood: {parsed}");
        return null;
    }
    var list = parsed.Data!.Orgs.Count == 0
        ? "no orgs"
        : string.Join(", ", parsed.Data.Orgs.Select(o => (o.Sid ?? "REDACTED") + (o.IsMain ? "*" : "")));
    summary.Add($"player {parsed.Data.Handle}: {list}");
    return parsed.Data;
}

async Task CaptureOrg(string sid)
{
    var (code, text) = await Capture($"org-{sid}.html", $"https://{Host}/en/orgs/{sid}", saveStatus: $"org-{sid}.status.txt");
    if (code == 404)
    {
        summary.Add($"org {sid}: not found (HTTP 404)");
        return;
    }
    string orgLine = $"org {sid}: no page (HTTP {code?.ToString() ?? "none"})";
    if (text is not null)
    {
        var info = OrgPageParser.Parse(text, sid);
        orgLine = info.IsFound
            ? $"org {sid}: \"{info.Data!.LongName}\", {info.Data.MemberCount?.ToString() ?? "?"} members, " +
              $"{info.Data.Archetype ?? "no archetype"} / {info.Data.Commitment ?? "no commitment"}" +
              $"{(info.Data.IsExclusive ? " / Exclusive" : "")}, activities: {(info.Data.Activities.Count == 0 ? "none" : string.Join(", ", info.Data.Activities))}"
            : $"org {sid}: page saved but not understood: {info}";
    }

    int membersRead = 0;
    int? total = null;
    for (int page = 1; page <= memberPages; page++)
    {
        var (mcode, json) = await Capture($"members-{sid}-page{page}.json", $"https://{Host}/api/orgs/getOrgMembers", jsonBody: MembersBody(sid, page));
        if (json is null)
        {
            orgLine += $"; members page {page}: HTTP {mcode?.ToString() ?? "none"}";
            break;
        }
        var members = OrgMembersParser.ParseResponse(json);
        if (!members.IsFound)
        {
            orgLine += $"; members page {page} not understood: {members}";
            break;
        }
        membersRead += members.Data!.Handles.Count;
        total ??= members.Data.TotalCount;
        if (members.Data.EntryCount < 32)
            break;
    }
    summary.Add(orgLine + $"; {membersRead} member handles read{(total is int t ? $" (RSI reports {t})" : "")}");
}

static string MembersBody(string sid, int page) =>
    "{\"symbol\":\"" + sid + "\",\"search\":\"\",\"pagesize\":32,\"page\":" + page + "}";

static IEnumerable<string> SplitNames(string list) =>
    list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// ------------------------------------------------------------------ HTTP

/// <summary>
/// One polite request. Saves the body to <paramref name="fileName"/> on HTTP 2xx (or only the status code when
/// <paramref name="statusOnly"/>), and the status code to <paramref name="saveStatus"/> when given.
/// Returns the status code (null if no answer) and the body text on success.
/// </summary>
async Task<(int? Code, string? Text)> Capture(string fileName, string url, string? jsonBody = null, bool statusOnly = false, string? saveStatus = null)
{
    if (!firstRequest)
        await Task.Delay(pause);
    firstRequest = false;

    var uri = new Uri(url);
    if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("This tool only contacts https://" + Host);

    var method = jsonBody is null ? HttpMethod.Get : HttpMethod.Post;
    Console.Write($"{method} {url}{(jsonBody is null ? "" : " " + jsonBody)} ... ");
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
                return (null, null);
            }
            var next = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(uri, location.OriginalString);
            if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, Host, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"redirect to {next.Host} refused; not saved");
                problems++;
                return (null, null);
            }
            Console.Write($"redirect -> {next.AbsoluteUri} ... ");
            await Task.Delay(pause);
            uri = next;
            response = await Send(method, uri, jsonBody);
        }
        using var answer = response;

        int code = (int)answer.StatusCode;
        if (saveStatus is not null)
            File.WriteAllText(Path.Combine(outputFolder, saveStatus), code.ToString());

        var path = Path.Combine(outputFolder, fileName);
        if (statusOnly)
        {
            File.WriteAllText(path, code.ToString());
            Console.WriteLine($"HTTP {code} -> {fileName}");
            return (code, null);
        }

        if (!answer.IsSuccessStatusCode)
        {
            Console.WriteLine($"HTTP {code}{(code == 404 ? " (not found)" : "; not saved")}");
            if (code != 404)
                problems++;
            return (code, null);
        }

        var text = await answer.Content.ReadAsStringAsync();
        File.WriteAllText(path, text, new UTF8Encoding(false));
        Console.WriteLine($"HTTP {code} -> {fileName} ({text.Length:N0} characters)");
        return (code, text);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"failed: {ex.Message}");
        problems++;
        return (null, null);
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
            var tests = Path.Combine(dir.FullName, "tests", "Iris.Core.Tests");
            if (Directory.Exists(tests))
                return Path.Combine(tests, "Fixtures", "rsi");
        }
    }
    return null;
}
