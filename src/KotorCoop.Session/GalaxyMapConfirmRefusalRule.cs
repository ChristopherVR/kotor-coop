namespace KotorCoop.Session;

/// <summary>
/// The pure decision behind Tier 2 item 18's galaxy-map gating (second half, task 2): should a
/// CLIENT's own confirm on the galaxy map (<c>CSWGuiInGameGalaxyMap</c>'s accept button, per
/// docs/research/galaxy-map-and-party-select.md §1) be refused right now?
///
/// Gated the same way <see cref="ClientDoorRefusalRule"/> refuses a client's own premature door
/// crossing, but deliberately SIMPLER: under <see cref="FollowLeadMode.Host"/> the client never
/// leads anything, door or galaxy map alike, so the confirm is refused unconditionally, exactly
/// mirroring <see cref="ClientDoorRefusalRule.HostMustLead"/>. Under <see cref="FollowLeadMode.Any"/>
/// this does NOT reach for <see cref="ClientDoorRefusalRule"/>'s extra "is the host busy right
/// now" gate (<see cref="LeadFollowRule.HostMaySafelyFollow"/>) — confirming a planet is not
/// itself the moment the host would have to interrupt anything to join: per
/// docs/research/galaxy-map-and-party-select.md §1.4, a confirm starts the SAME
/// announce-then-follow chain (<c>move_to_module_pending</c>) an ordinary door crossing already
/// uses, so the host's own busy-ness is already checked at the point it would actually follow
/// (inside <see cref="LeadFollowRule.DecideHostFollow"/>), not a second time here. So under
/// <c>any</c> this always returns false: either player may already lead an ordinary door
/// crossing the other follows, and the same latitude extends to a galaxy-map confirm.
/// </summary>
public static class GalaxyMapConfirmRefusalRule
{
    /// <param name="isClient">This instance does not own enemies/encounters — the same
    /// <c>!session.IsEnemyAuthority</c> test <see cref="ClientDoorRefusalRule"/> uses.</param>
    /// <param name="mode">coop.ini <c>follow_lead</c> (<see cref="AgentSettings.FollowLeadMode"/>).</param>
    public static bool ShouldRefuse(bool isClient, FollowLeadMode mode) =>
        isClient && mode == FollowLeadMode.Host;
}
