using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Iris.Core.Rsi;
using Tomlyn;
using Tomlyn.Model;

namespace Iris.Core.Config;

/// <summary>Result of reading a config file: the config (if valid) plus every problem found.</summary>
public sealed class ConfigLoadResult
{
    public SocialConfig? Config { get; init; }
    public List<string> Errors { get; } = new();

    /// <summary>Non-fatal notes, e.g. unknown keys from a newer app version.</summary>
    public List<string> Warnings { get; } = new();

    public bool IsValid => Errors.Count == 0 && Config is not null;
}

/// <summary>
/// Reads and writes the shareable Social Overlay config file (TOML).
///
/// Example file:
/// <code>
/// format = "sc-social-overlay"
/// format_version = 1
/// name = "My squad list"
///
/// [[players]]
/// handle = "Nitebird"
/// color = "Cyan"
///
/// [[orgs]]
/// sid = "J0K3R5"
/// priority = 1
/// color = "Red"
///
/// [[org_rules]]
/// kind = "archetype"
/// value = "PMC"
/// priority = 2
/// color = "Orange"
///
/// [[org_rules]]
/// kind = "no_match"
/// priority = 10
/// color = "Blue"
/// </code>
/// Every file has exactly one <c>no_match</c> rule, last, priority 10 (see <see cref="NoMatchRule"/>). Reading a file without one
/// adds it (Blue) silently; writing always writes it.
/// </summary>
public static class SocialConfigToml
{
    public const string FormatId = "sc-social-overlay";
    public const int FormatVersion = 1;

    // RSI handles and SIDs: letters, digits, hyphen, underscore. No spaces or other symbols.
    private static readonly Regex HandleRe = new(
        "^[A-Za-z0-9_-]{" + RsiNames.MinHandleLength + "," + RsiNames.MaxHandleLength + "}$", RegexOptions.Compiled);
    private static readonly Regex SidRe = new("^[A-Za-z0-9_-]{1,20}$", RegexOptions.Compiled);

    // ------------------------------------------------------------------ write

    public static string Write(SocialConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Star Citizen Social Overlay list. Shareable: contains no personal settings.");
        sb.AppendLine($"format = {Quote(FormatId)}");
        sb.AppendLine($"format_version = {FormatVersion}");
        if (!string.IsNullOrWhiteSpace(config.Name))
            sb.AppendLine($"name = {Quote(config.Name!)}");

        foreach (var p in config.Players)
        {
            sb.AppendLine();
            sb.AppendLine("[[players]]");
            sb.AppendLine($"handle = {Quote(p.Handle)}");
            sb.AppendLine($"color = {Quote(p.Color.ToString())}");
        }

        foreach (var o in config.Orgs)
        {
            sb.AppendLine();
            sb.AppendLine("[[orgs]]");
            sb.AppendLine($"sid = {Quote(o.Sid)}");
            sb.AppendLine($"priority = {o.Priority.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"color = {Quote(o.Color.ToString())}");
        }

        // The No Match rule is written last, exactly once, with priority 10 (the default is written when the config has none).
        foreach (var r in config.OrgRules.Where(r => r.Kind != AttributeKind.NoMatch)
                     .Append(NoMatchRule.Find(config) ?? NoMatchRule.CreateDefault()))
        {
            sb.AppendLine();
            sb.AppendLine("[[org_rules]]");
            sb.AppendLine($"kind = {Quote(KindToText(r.Kind))}");
            if (r.Value is not null && r.Kind != AttributeKind.NoMatch)
                sb.AppendLine($"value = {Quote(r.Value)}");
            var priority = r.Kind == AttributeKind.NoMatch ? NoMatchRule.Priority : r.Priority;
            sb.AppendLine($"priority = {priority.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"color = {Quote(r.Color.ToString())}");
        }

        return sb.ToString();
    }

    /// <summary>TOML basic string with every special character escaped.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ------------------------------------------------------------------ read

    public static ConfigLoadResult Read(string text)
    {
        var doc = Toml.Parse(text);
        if (doc.HasErrors)
        {
            var bad = new ConfigLoadResult();
            foreach (var d in doc.Diagnostics)
                bad.Errors.Add("TOML syntax: " + d);
            return bad;
        }

        TomlTable root;
        try
        {
            root = Toml.ToModel(doc);
        }
        catch (Exception ex)
        {
            var bad = new ConfigLoadResult();
            bad.Errors.Add("TOML structure: " + ex.Message);
            return bad;
        }

        var config = new SocialConfig();
        var result = new ConfigLoadResult { Config = config };
        var errors = result.Errors;
        var warnings = result.Warnings;

        // header
        if (!root.TryGetValue("format", out var fmt) || fmt as string != FormatId)
            errors.Add($"format: must be \"{FormatId}\" (this is not a Social Overlay config file).");
        if (!root.TryGetValue("format_version", out var ver) || ver is not long v)
            errors.Add("format_version: missing or not a whole number.");
        else if (v < 1 || v > FormatVersion)
            errors.Add($"format_version: {v} is not supported (this app reads version {FormatVersion}).");
        if (root.TryGetValue("name", out var name))
        {
            if (name is string n) config.Name = n;
            else errors.Add("name: must be text.");
        }
        WarnUnknown(root, "", warnings, "format", "format_version", "name", "players", "orgs", "org_rules");

        // players
        var seenHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, path) in Tables(root, "players", errors))
        {
            var handle = GetString(t, "handle", path, errors);
            var color = GetColor(t, path, errors);
            WarnUnknown(t, path, warnings, "handle", "color");
            if (handle is null || color is null) continue;
            if (!HandleRe.IsMatch(handle))
                errors.Add($"{path}.handle: \"{handle}\" is not a valid handle (4 to 25 characters: letters, digits, - and _ only).");
            else if (!seenHandles.Add(handle))
                errors.Add($"{path}.handle: \"{handle}\" is listed more than once.");
            else
                config.Players.Add(new PlayerEntry(handle, color.Value));
        }

        // orgs
        var seenSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, path) in Tables(root, "orgs", errors))
        {
            var sid = GetString(t, "sid", path, errors);
            var prio = GetPriority(t, path, errors);
            var color = GetColor(t, path, errors);
            WarnUnknown(t, path, warnings, "sid", "priority", "color");
            if (sid is null || prio is null || color is null) continue;
            if (!SidRe.IsMatch(sid))
                errors.Add($"{path}.sid: \"{sid}\" is not a valid org SID (letters, digits, - and _ only).");
            else if (!seenSids.Add(sid))
                errors.Add($"{path}.sid: \"{sid}\" is listed more than once.");
            else
                config.Orgs.Add(new OrgEntry(sid, prio.Value, color.Value));
        }

        // org-attribute rules
        OrgAttributeRule? noMatch = null;
        foreach (var (t, path) in Tables(root, "org_rules", errors))
        {
            var kindText = GetString(t, "kind", path, errors);
            var kind = kindText is null ? null : TextToKind(kindText);

            // The No Match rule's priority is fixed (10), so it is not read as an ordinary 1-9 priority.
            var prio = kind == AttributeKind.NoMatch ? null : GetPriority(t, path, errors);
            var color = GetColor(t, path, errors);
            WarnUnknown(t, path, warnings, "kind", "value", "priority", "color");
            if (kindText is null || color is null || (kind != AttributeKind.NoMatch && prio is null)) continue;
            if (kind is null)
            {
                errors.Add($"{path}.kind: \"{kindText}\" must be one of archetype, activity, redacted, no_org, no_match.");
                continue;
            }

            if (kind == AttributeKind.NoMatch)
            {
                if (t.ContainsKey("value"))
                    warnings.Add($"{path}.value: ignored for kind \"{kindText}\".");
                if (t.TryGetValue("priority", out var rawPriority) && !(rawPriority is long p && p == NoMatchRule.Priority))
                    warnings.Add($"{path}.priority: the No Match rule always has priority {NoMatchRule.Priority}; using {NoMatchRule.Priority}.");
                if (noMatch is not null)
                    warnings.Add($"{path}: only one \"no_match\" rule is allowed; this one is ignored (the first is kept).");
                else
                    noMatch = new OrgAttributeRule(AttributeKind.NoMatch, null, NoMatchRule.Priority, color.Value);
                continue;
            }

            string? value = null;
            if (t.TryGetValue("value", out var raw))
            {
                if (raw is not string s || s.Trim().Length == 0)
                {
                    errors.Add($"{path}.value: must be non-empty text.");
                    continue;
                }
                value = s.Trim();
            }
            bool needsValue = kind is AttributeKind.Archetype or AttributeKind.Activity;
            if (needsValue && value is null)
            {
                errors.Add($"{path}.value: required for kind \"{kindText}\" (e.g. \"PMC\").");
                continue;
            }
            if (!needsValue && value is not null)
                warnings.Add($"{path}.value: ignored for kind \"{kindText}\".");
            config.OrgRules.Add(new OrgAttributeRule(kind.Value, needsValue ? value : null, prio!.Value, color.Value));
        }

        // Exactly one No Match rule, last. A file without one gets the default (Blue) silently.
        config.OrgRules.Add(noMatch ?? NoMatchRule.CreateDefault());

        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<(TomlTable table, string path)> Tables(TomlTable root, string key, List<string> errors)
    {
        if (!root.TryGetValue(key, out var raw))
            yield break;
        if (raw is not TomlTableArray arr)
        {
            errors.Add($"{key}: must be a list of [[{key}]] entries.");
            yield break;
        }
        for (int i = 0; i < arr.Count; i++)
            yield return (arr[i], $"{key}[{i + 1}]");
    }

    private static string? GetString(TomlTable t, string key, string path, List<string> errors)
    {
        if (!t.TryGetValue(key, out var raw))
        {
            errors.Add($"{path}.{key}: missing.");
            return null;
        }
        if (raw is not string s || s.Trim().Length == 0)
        {
            errors.Add($"{path}.{key}: must be non-empty text.");
            return null;
        }
        return s.Trim();
    }

    private static int? GetPriority(TomlTable t, string path, List<string> errors)
    {
        if (!t.TryGetValue("priority", out var raw) || raw is not long p)
        {
            errors.Add($"{path}.priority: missing or not a whole number.");
            return null;
        }
        if (p < 1 || p > 9)
        {
            errors.Add($"{path}.priority: {p} must be between 1 (highest) and 9.");
            return null;
        }
        return (int)p;
    }

    private static OverlayColor? GetColor(TomlTable t, string path, List<string> errors)
    {
        if (!t.TryGetValue("color", out var raw) || raw is not string s)
        {
            errors.Add($"{path}.color: missing.");
            return null;
        }
        if (Enum.TryParse<OverlayColor>(s.Trim(), ignoreCase: true, out var c) && Enum.IsDefined(c))
            return c;
        errors.Add($"{path}.color: \"{s}\" must be one of {string.Join(", ", Enum.GetNames<OverlayColor>())}.");
        return null;
    }

    private static void WarnUnknown(TomlTable t, string path, List<string> warnings, params string[] known)
    {
        foreach (var key in t.Keys)
            if (!known.Contains(key))
                warnings.Add($"{(path.Length > 0 ? path + "." : "")}{key}: unknown setting, ignored.");
    }

    public static string KindToText(AttributeKind k) => k switch
    {
        AttributeKind.Archetype => "archetype",
        AttributeKind.Activity => "activity",
        AttributeKind.Redacted => "redacted",
        AttributeKind.NoOrg => "no_org",
        AttributeKind.NoMatch => "no_match",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static AttributeKind? TextToKind(string s) => s.Trim().ToLowerInvariant() switch
    {
        "archetype" => AttributeKind.Archetype,
        "activity" => AttributeKind.Activity,
        "redacted" => AttributeKind.Redacted,
        "no_org" => AttributeKind.NoOrg,
        "no_match" => AttributeKind.NoMatch,
        _ => null,
    };
}
