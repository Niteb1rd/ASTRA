using Iris.Core.Config;
using Iris.Core.Social;

namespace Iris.Core.Overlay;

/// <summary>
/// Builds the sample overlay shown by "Show test overlay (15 s)" from the user's CURRENT lists:
/// up to 3 players of the player list ({ }), up to 2 org-list examples ([ ], an org SID from the list with a made-up handle),
/// 1 org-attribute example (&lt; &gt;, only when a rule exists) and 2 No Match examples (TestPilot01 and TestPilot02, "Orgs: not looked up").
/// The nameplate boxes sit at plausible sizes inside the default scan region (the middle 70% of the width, from 10% below the top
/// to 30% above the bottom), scaled to the target screen.
/// The colors, brackets and orgs lines come from the same code the real overlay uses (<see cref="OverlayModelBuilder"/>).
/// </summary>
public static class TestOverlayBuilder
{
    public const double ScanLeftPercent = 15;
    public const double ScanRightPercent = 15;
    public const double ScanTopPercent = 10;
    public const double ScanBottomPercent = 30;

    public const string OrgExampleHandlePrefix = "TestOrgMember";
    public const string AttributeExampleHandle = "TestAttrPilot01";
    public const string NoMatchExampleHandle1 = "TestPilot01";
    public const string NoMatchExampleHandle2 = "TestPilot02";

    // Where the n-th example goes inside its grid cell (4 columns x 2 rows), as a share of the cell: scattered, but the same every time.
    private static readonly (double X, double Y)[] Jitter =
    {
        (-0.10, 0.12), (0.08, -0.10), (-0.04, 0.05), (0.12, 0.10),
        (0.06, -0.12), (-0.12, 0.08), (0.10, -0.04), (-0.06, -0.10),
    };

    private const int Columns = 4;
    private const int Rows = 2;

    /// <param name="config">The lists to take examples from.</param>
    /// <param name="lookupCachedOrgs">Cache lookup for real handles (null: nothing cached).</param>
    /// <param name="screenWidth">Width in pixels of the screen or game window the overlay will cover.</param>
    /// <param name="screenHeight">Height in pixels.</param>
    public static OverlayModel Build(
        SocialConfig config,
        Func<string, IReadOnlyList<OrgMembership>?> lookupCachedOrgs,
        double screenWidth,
        double screenHeight)
    {
        var made = new Dictionary<string, IReadOnlyList<OrgMembership>>(StringComparer.OrdinalIgnoreCase);
        var handles = new List<string>();

        foreach (var player in config.Players.Take(3))
            handles.Add(player.Handle);

        var number = 1;
        foreach (var org in config.Orgs.Take(2))
        {
            var handle = OrgExampleHandlePrefix + number.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            number++;
            made[handle] = new[] { new OrgMembership(org.Sid, true) };
            handles.Add(handle);
        }

        var rule = config.OrgRules.FirstOrDefault(r => r.Kind != AttributeKind.NoMatch);
        if (rule is not null)
        {
            made[AttributeExampleHandle] = OrgsFor(rule);
            handles.Add(AttributeExampleHandle);
        }

        handles.Add(NoMatchExampleHandle1);
        handles.Add(NoMatchExampleHandle2);

        var detections = new List<DetectedPlayer>(handles.Count);
        for (var i = 0; i < handles.Count; i++)
            detections.Add(new DetectedPlayer(handles[i], BoxFor(i, handles[i], screenWidth, screenHeight)));

        IReadOnlyList<OrgMembership>? Lookup(string handle) =>
            made.TryGetValue(handle, out var orgs) ? orgs : lookupCachedOrgs(handle);

        return OverlayModelBuilder.Build(detections, config, Lookup);
    }

    /// <summary>Made-up orgs for a player who matches <paramref name="rule"/> (when no better rule or list entry gets in the way).</summary>
    private static IReadOnlyList<OrgMembership> OrgsFor(OrgAttributeRule rule) => rule.Kind switch
    {
        AttributeKind.NoOrg => Array.Empty<OrgMembership>(),
        AttributeKind.Redacted => new[] { new OrgMembership(null, true) },
        AttributeKind.Archetype => new[] { new OrgMembership("TESTORG", true, rule.Value) },
        AttributeKind.Activity => new[] { new OrgMembership("TESTORG", true, null, new[] { rule.Value ?? string.Empty }) },
        _ => Array.Empty<OrgMembership>(),
    };

    /// <summary>A nameplate-sized box for the i-th example, inside the scan region and inside its grid cell.</summary>
    public static PixelRect BoxFor(int index, string handle, double screenWidth, double screenHeight)
    {
        var regionLeft = screenWidth * ScanLeftPercent / 100.0;
        var regionRight = screenWidth - screenWidth * ScanRightPercent / 100.0;
        var regionTop = screenHeight * ScanTopPercent / 100.0;
        var regionBottom = screenHeight - screenHeight * ScanBottomPercent / 100.0;
        var cellWidth = (regionRight - regionLeft) / Columns;
        var cellHeight = (regionBottom - regionTop) / Rows;

        // A nameplate is two lines (name, distance); its width grows with the name. About 46 x 160 px at 1440p for a 12-letter name.
        var boxHeight = screenHeight * 0.032;
        var boxWidth = Math.Min(screenHeight * (0.04 + 0.0055 * handle.Length), cellWidth * 0.9);

        var slot = index % (Columns * Rows);
        var column = slot % Columns;
        var row = slot / Columns;
        var (jx, jy) = Jitter[slot];
        var centerX = regionLeft + (column + 0.5 + jx) * cellWidth;
        var centerY = regionTop + (row + 0.5 + jy) * cellHeight;

        var x = Math.Clamp(centerX - boxWidth / 2, regionLeft, Math.Max(regionLeft, regionRight - boxWidth));
        var y = Math.Clamp(centerY - boxHeight / 2, regionTop, Math.Max(regionTop, regionBottom - boxHeight));
        return new PixelRect(x, y, boxWidth, boxHeight);
    }
}
