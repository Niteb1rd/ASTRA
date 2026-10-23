using Iris.Core.Config;
using Iris.Core.Social;

namespace Iris.Core.Overlay;

/// <summary>
/// Turns detected nameplates into an <see cref="OverlayModel"/>: one bracket per nameplate, one panel entry per player.
/// - Every bracketed player is in the panel, No Match players too.
/// - Orgs come from <c>lookupOrgs</c> (the cache); null means the cache has nothing for that player, and the orgs line is
///   <see cref="PanelText.NotLookedUp"/>. Such a player can only match the player list, otherwise it is No Match.
/// - Panel order: player list, org list, org attribute, No Match (the order of <see cref="MatchKind"/>); inside a group, the order
///   the players were first seen in. A player seen twice has one panel entry (the first one counts), but every nameplate keeps its bracket.
/// </summary>
public static class OverlayModelBuilder
{
    public static OverlayModel Build(
        IReadOnlyList<DetectedPlayer> detections,
        SocialConfig config,
        Func<string, IReadOnlyList<OrgMembership>?> lookupOrgs)
    {
        var brackets = new List<BracketItem>(detections.Count);
        var entries = new List<PanelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var detected in detections)
        {
            var orgs = lookupOrgs(detected.Handle);
            var match = orgs is null
                ? ColorResolver.ResolveUnknown(detected.Handle, config)
                : ColorResolver.Resolve(detected.Handle, orgs, config);

            brackets.Add(new BracketItem(detected.Box, match.Kind, match.Color));

            if (seen.Add(detected.Handle))
            {
                var line = orgs is null ? PanelText.NotLookedUp : PanelText.OrgLine(orgs, match);
                entries.Add(new PanelEntry(detected.Handle, match.Kind, match.Color, line));
            }
        }

        // OrderBy is stable: players of the same kind stay in first-seen order.
        var ordered = entries.OrderBy(e => (int)e.Kind).ToList();
        return new OverlayModel(brackets, ordered);
    }
}
