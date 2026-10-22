using System.Text.Json;
using AngleSharp.Dom;

namespace Iris.Core.Rsi;

/// <summary>
/// Reads one page of RSI's members API (POST /api/orgs/getOrgMembers) from saved text.
/// Pure: JSON text in, <see cref="OrgMembersPage"/> out. No network.
///
/// The JSON looks like {"success":1,"code":"OK","msg":"OK","data":{"html":"...member entries...","totalrows":104}}.
/// The "html" part is a fragment holding one entry per member; hidden members have no handle and are skipped.
/// </summary>
public static class OrgMembersParser
{
    // ======================================================================================
    // RESPONSE / LAYOUT ASSUMPTIONS. Every JSON field name, CSS selector and label string used
    // by this parser is here. They are educated guesses and MUST be checked against the captured
    // fixture (tests/Iris.Core.Tests/Fixtures/rsi/members-J0K3R5-page1.json).
    // ======================================================================================

    private const string SuccessField = "success";
    private const string MessageField = "msg";
    private const string DataField = "data";
    private const string HtmlField = "html";
    private const string TotalField = "totalrows";

    /// <summary>One element per member, hidden members included.</summary>
    private const string MemberEntrySelector = ".member-item";

    /// <summary>The link on a visible member; its last path segment is the handle.</summary>
    private const string MemberLinkSelector = "a[href*='/citizens/']";

    /// <summary>Fallback place for the handle when there is no link.</summary>
    private const string MemberNickSelector = ".nick";

    /// <summary>A class name fragment RSI puts on hidden (redacted) entries.</summary>
    private const string HiddenClassMarker = "visibility-R";

    private const string RedactedText = "REDACTED";

    /// <summary>Parses the whole API response (JSON wrapper plus HTML fragment).</summary>
    public static RsiResult<OrgMembersPage> ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return RsiResult<OrgMembersPage>.Failed("members response is not a JSON object");

            int? success = root.TryGetProperty(SuccessField, out var successElement) ? ReadInt(successElement) : null;
            if (success != 1)
            {
                var message = root.TryGetProperty(MessageField, out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString()
                    : null;
                return RsiResult<OrgMembersPage>.Failed($"members API reported failure ({message ?? "no message"})");
            }

            if (!root.TryGetProperty(DataField, out var data) || data.ValueKind != JsonValueKind.Object)
                return RsiResult<OrgMembersPage>.Failed("members response has no data");
            if (!data.TryGetProperty(HtmlField, out var htmlElement) || htmlElement.ValueKind != JsonValueKind.String)
                return RsiResult<OrgMembersPage>.Failed("members response has no html");

            int? total = data.TryGetProperty(TotalField, out var totalElement) ? ReadInt(totalElement) : null;
            return RsiResult<OrgMembersPage>.Found(ParseFragment(htmlElement.GetString() ?? "", total));
        }
        catch (JsonException ex)
        {
            return RsiResult<OrgMembersPage>.Failed($"members response is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Parses just the HTML fragment of member entries.</summary>
    public static OrgMembersPage ParseFragment(string html, int? totalCount = null)
    {
        var doc = HtmlText.Parse(html);
        var handles = new List<string>();
        int entries = 0;
        foreach (var entry in doc.QuerySelectorAll(MemberEntrySelector))
        {
            entries++;
            var handle = HandleOf(entry);
            if (handle is not null)
                handles.Add(handle);
        }
        return new OrgMembersPage(handles, entries, totalCount);
    }

    private static string? HandleOf(IElement entry)
    {
        if ((entry.ClassName ?? "").Contains(HiddenClassMarker, StringComparison.Ordinal))
            return null;

        var href = entry.QuerySelector(MemberLinkSelector)?.GetAttribute("href");
        if (href is not null)
        {
            var path = href.Split('?', '#')[0].TrimEnd('/');
            var segment = Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1));
            if (RsiNames.IsValidHandle(segment))
                return segment;
        }

        var nick = HtmlText.Of(entry.QuerySelector(MemberNickSelector));
        if (nick.Length > 0
            && !string.Equals(nick, RedactedText, StringComparison.OrdinalIgnoreCase)
            && RsiNames.IsValidHandle(nick))
            return nick;

        return null;
    }

    /// <summary>Reads a JSON number or numeric string as an int. Anything else gives null.</summary>
    private static int? ReadInt(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out var n) ? n : null;
            case JsonValueKind.String:
                return int.TryParse(element.GetString(), out var s) ? s : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }
}
