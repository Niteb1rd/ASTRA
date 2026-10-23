using System.Globalization;
using System.Xml;

namespace Iris.Core.Overlay;

/// <summary>Star Citizen's own "Display Mode" setting (the <c>WindowMode</c> value in attributes.xml).</summary>
public enum WindowMode
{
    Windowed = 0,
    Borderless = 1,
    Fullscreen = 2,
}

/// <summary>
/// Where Star Citizen keeps its display setting and how to read it. Pure functions (the file system is passed in), so they are tested
/// without a game. The file is only ever read, never written.
/// </summary>
public static class StarCitizenDisplaySettings
{
    /// <summary>The folder the game's exe sits in: <c>&lt;install&gt;\Bin64\StarCitizen.exe</c>.</summary>
    public const string BinFolderName = "Bin64";

    /// <summary>The attribute that holds the mode: <c>&lt;Attr name="WindowMode" value="1"/&gt;</c>.</summary>
    public const string AttributeName = "WindowMode";

    private const string ClientFolder = "client";
    private const string PreferredProfileFolder = "0";

    /// <summary>
    /// The attributes.xml that belongs to the running game, found relative to ITS exe (several installs - LIVE, PTU, EPTU - can exist side by side):
    /// <c>&lt;install&gt;\user\client\0\Profiles\default\attributes.xml</c>, where install is the parent of Bin64. If that file does not exist, the most
    /// recently modified <c>&lt;install&gt;\user\client\*\Profiles\default\attributes.xml</c> is used. Null when the exe is not in a Bin64 folder or no file exists.
    /// </summary>
    /// <param name="exePath">Full path of the running StarCitizen.exe.</param>
    /// <param name="fileExists">Says whether a file exists.</param>
    /// <param name="findCandidates">Given the <c>&lt;install&gt;\user\client</c> folder, lists every existing <c>*\Profiles\default\attributes.xml</c> under it with its last write time (UTC); empty when the folder does not exist.</param>
    public static string? AttributesPathFor(
        string exePath,
        Func<string, bool> fileExists,
        Func<string, IEnumerable<(string path, DateTime modifiedUtc)>> findCandidates)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return null;

        var binDirectory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(binDirectory) || !string.Equals(Path.GetFileName(binDirectory), BinFolderName, StringComparison.OrdinalIgnoreCase))
            return null;
        var install = Path.GetDirectoryName(binDirectory);
        if (string.IsNullOrEmpty(install))
            return null;

        var clientDirectory = Path.Combine(install, "user", ClientFolder);
        var preferred = Path.Combine(clientDirectory, PreferredProfileFolder, "Profiles", "default", "attributes.xml");
        if (fileExists(preferred))
            return preferred;

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var (path, modifiedUtc) in findCandidates(clientDirectory))
        {
            if (best is null || modifiedUtc > bestTime)
            {
                best = path;
                bestTime = modifiedUtc;
            }
        }
        return best;
    }

    /// <summary>
    /// The mode in the text of an attributes.xml: the <c>value</c> of the first <c>&lt;Attr name="WindowMode" .../&gt;</c>. Null when the text is not
    /// valid XML (before that element), the element is missing, the value is not a whole number or it is not 0, 1 or 2.
    /// The XML is parsed without DTDs and without resolving anything external.
    /// </summary>
    public static WindowMode? ParseWindowMode(string? xmlText)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
            return null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };

        try
        {
            using var text = new StringReader(xmlText);
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Attr")
                    continue;
                if (reader.GetAttribute("name") != AttributeName)
                    continue;

                var value = reader.GetAttribute("value");
                if (value is null || !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    return null;
                return number switch
                {
                    0 => WindowMode.Windowed,
                    1 => WindowMode.Borderless,
                    2 => WindowMode.Fullscreen,
                    _ => null,
                };
            }
            return null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return null; // not XML, or XML that is not allowed (a DTD): treated as "unknown"
        }
    }
}
