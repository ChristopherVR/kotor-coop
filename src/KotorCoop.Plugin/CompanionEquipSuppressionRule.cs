namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision behind <see cref="CompanionEquipSuppressionController"/> (coop.ini
/// <c>companion_equip_host_only</c>, default ON, NEEDS-LIVE): should THIS instance close its own
/// copy of the retail Equipment screen because it is showing a COMPANION?
///
/// Companion gear is host-owned (<c>companion_gear_sync</c>: the host publishes, a client
/// dresses its mirror through <c>EquipItemFromTemplateDirect</c>) and the party pool is
/// host-authoritative (shared-inventory v2). A client that equips a pool item on Carth takes
/// it out of the pool — an intent the host applies — and then the host's next gear publish
/// re-dresses the companion from the host's own template: the joiner's item is gone from the
/// pool and orphaned on a mirror body that is re-dressed at the next transition
/// (docs/review-seamless-2026-09-18.md §2). The joiner's OWN character stays fully editable;
/// only the companion pages of the screen are refused.
///
/// Kept separate from the controller for the same reason <see cref="PartySelectSuppressionRule"/>
/// is: a wrong verdict is invisible until it is live, so the arithmetic is pinned by tests
/// first. Two states drive it — the SUBJECT the screen is showing this pass, and an ARMED
/// latch that guarantees ONE close per visit (the screen has to be observed closed, or showing
/// the PC, before another close is allowed), so a close the engine refuses can never turn into
/// the 500-a-second write storm docs/research/galaxy-map-and-party-select.md's party-select
/// pass found.
/// </summary>
internal static class CompanionEquipSuppressionRule
{
    public enum Action
    {
        /// <summary>Leave the screen alone.</summary>
        None,

        /// <summary>Close it and tell the player the host equips companions.</summary>
        Close,

        /// <summary>A guard refused a pointer — stop reading for the rest of the session.</summary>
        Disable,
    }

    /// <param name="enabled">coop.ini <c>companion_equip_host_only</c>.</param>
    /// <param name="isClientInLiveSession"><c>SharedBlockView.TryGetHostPartyRoster</c>'s valid
    /// bit — the same "I am a client with a live host roster" signal
    /// <see cref="PartySelectSuppressionRule"/> uses; a host is the one who is SUPPOSED to
    /// equip companions.</param>
    /// <param name="subject">What the screen shows this pass.</param>
    /// <param name="armed">The one-close-per-visit latch, updated in place.</param>
    public static Action Decide(
        bool enabled, bool isClientInLiveSession, Odyssey.EquipScreenSubject subject, ref bool armed)
    {
        if (!enabled || !isClientInLiveSession)
        {
            return Action.None;
        }

        switch (subject)
        {
            case Odyssey.EquipScreenSubject.Unreadable:
                return Action.Disable;

            case Odyssey.EquipScreenSubject.NotOpen:
            case Odyssey.EquipScreenSubject.Player:
                // The visit ended, or the player is back on their own page: re-arm.
                armed = true;
                return Action.None;

            case Odyssey.EquipScreenSubject.Companion:
                if (!armed)
                {
                    return Action.None;
                }

                armed = false;
                return Action.Close;

            default:
                return Action.None;
        }
    }
}
