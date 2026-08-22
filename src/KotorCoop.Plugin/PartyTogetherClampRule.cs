namespace KotorCoop.Plugin;

/// <summary>
/// The decision half of <c>PartyRosterController.SyncClientPartyTogetherState</c>'s CSWParty
/// count clamp, separated so it can be unit-tested — the same split <c>ControlReturnRule</c>
/// and <c>PartyFreezeRule</c> use.
///
/// ⚠ THE BUG THIS CLOSES. The clamp trims whichever member sits at the END of CSWParty's array
/// (member[count-1]) down to CSWPartyTable's own companion count — see that controller's own
/// doc comment: "Clamping CSWParty to the table's count drops the LAST companion out of the
/// struct the selection UI, portraits and follow logic read." In the ordinary case the array
/// is ordered [PC, companion, companion], so "the last one" is always a companion, and trimming
/// it is exactly the harmless door-transition fix it was written for.
///
/// But a real "ChangeCharacter" reorder puts whichever creature is CURRENTLY CONTROLLED at
/// member[0] and shuffles everyone else down — so a client who was already playing AS a party
/// companion (Trask, say) at the moment of connect has the array the other way round:
/// [Trask, PC]. "The last one" is now the PC's own slot. The clamp does not know or care who
/// occupies which index; it trims by position. So the exact same fix that lets a client cross
/// a real door, applied at exactly this moment, trims the PC's own slot out of the struct that
/// both the engine's own control resolution and <c>ControlReturnController</c>'s camera
/// hand-back need intact — and the player stays locked onto the companion with no struct left
/// to switch back into. Reported live 2026-08-22, the same day the clamp went client-only
/// (b491b74): that change is what let the clamp reach this exact client scenario at all.
///
/// The fix: DEFER, not disable. The clamp is exactly as safe as it always was in the ordinary
/// case (PC at member[0]) and stays on the moment control is back — <c>ControlReturnController</c>
/// converges within its own bounded assert budget regardless, so the deferral is at most a few
/// hundred milliseconds and never a permanent trade-off.
/// </summary>
internal static class PartyTogetherClampRule
{
    /// <summary>
    /// Whether this pass should clamp CSWParty's member count down to
    /// <paramref name="tableCount"/>.
    ///
    /// <paramref name="controllingSessionOwnedCompanion"/> is "the creature the player is
    /// currently driving is one of this instance's own party-roster companions" — the same
    /// question <c>ControlReturnController</c> asks, asked the same way. While that is true the
    /// clamp is refused outright, REGARDLESS of the count comparison below: the member the clamp
    /// would trim is not reliably a companion in that state, and control-return owns getting the
    /// player back onto their PC first.
    ///
    /// Once it is false again (control is back, or was never lost), the ordinary rule applies
    /// unchanged: clamp only when CSWParty's own count reads higher than CSWPartyTable's — never
    /// lower, and never when the two already agree.
    /// </summary>
    public static bool ShouldClamp(bool controllingSessionOwnedCompanion, int clientCount, int tableCount) =>
        !controllingSessionOwnedCompanion && clientCount > tableCount;
}
