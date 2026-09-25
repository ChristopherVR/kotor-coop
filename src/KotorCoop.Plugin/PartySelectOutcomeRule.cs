namespace KotorCoop.Plugin;

/// <summary>Outcomes of a host-opened party-select screen, once it closes.</summary>
internal enum PartySelectOutcomeCode
{
    /// <summary>The screen is still open, or was never opened.</summary>
    Pending = 0,

    /// <summary>The roster differs from the snapshot taken at open — <c>AcceptParty</c> ran
    /// (docs/research/galaxy-map-and-party-select.md §2.3: it writes through the exact
    /// <c>SpawnNPC</c>/<c>AddMember</c> pair <c>PartyRosterController</c> already mirrors).</summary>
    Confirmed = 1,

    /// <summary>The roster is unchanged from the snapshot — the Back button, not Accept. A
    /// known limitation: a host who re-confirms the IDENTICAL party they already had reads the
    /// same as a cancel. Documented, not fixed this pass — see
    /// docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section.</summary>
    Cancelled = 2,
}

/// <summary>
/// The pure decision behind <see cref="PartySelectHostRequestController"/>'s "did the host
/// confirm or cancel" read: whether a host-opened <c>CSWGuiPartySelection</c> that has just
/// closed represents a confirm (the party-table roster changed since it opened) or a cancel
/// (it did not). No new engine offset — reuses <c>Odyssey.TryReadPartyRoster</c>'s existing
/// <c>partyMask</c>, the same signal <c>PartyRosterController</c> already polls every ~30
/// frames with no dependency on GUI/panel state at all (§2.4). Chosen over trying to
/// distinguish Accept from Back by RE'ing a new panel-internal flag: this reuses machinery
/// already proven live, at the cost of one known limitation (see
/// <see cref="PartySelectOutcomeCode.Cancelled"/>'s own doc comment).
/// </summary>
internal static class PartySelectOutcomeRule
{
    /// <param name="panelStillOpen">Whether the host's screen is still registered
    /// (<c>CSWGuiManager::PanelExists</c>) — while true, the outcome is
    /// <see cref="PartySelectOutcomeCode.Pending"/> regardless of the roster.</param>
    /// <param name="rosterMaskAtOpen">The party-table's <c>partyMask</c>
    /// (<c>Odyssey.TryReadPartyRoster</c>) captured the moment the screen opened.</param>
    /// <param name="rosterMaskNow">The same mask, read again after the screen closed.</param>
    public static PartySelectOutcomeCode Decide(bool panelStillOpen, uint rosterMaskAtOpen, uint rosterMaskNow)
    {
        if (panelStillOpen)
        {
            return PartySelectOutcomeCode.Pending;
        }

        return rosterMaskNow != rosterMaskAtOpen ? PartySelectOutcomeCode.Confirmed : PartySelectOutcomeCode.Cancelled;
    }
}
