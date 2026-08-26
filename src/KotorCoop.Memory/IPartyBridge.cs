// Party roster, companions, progression (gold/experience) and portraits.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IPartyBridge
{
    /// <summary>
    /// This instance's companion roster, straight off its own party table: which of the nine
    /// NPC slots are recruited, and which are in the active party. Default is "cannot read
    /// one", which is NOT the same as an empty roster and must never be published as one.
    /// </summary>
    bool TryReadLocalPartyRoster(out PartyRoster roster)
    {
        roster = PartyRoster.Empty;
        return false;
    }

    /// <summary>
    /// Hands the game the HOST's roster to reconcile against, or clears it.
    ///
    /// <paramref name="valid"/> false means "no authority is speaking here" — the host itself,
    /// a client with no level yet, a disabled channel — and the game must then leave its own
    /// roster completely alone. Default false.
    /// </summary>
    bool WriteHostPartyRoster(bool valid, PartyRoster roster) => false;

    /// <summary>False when this bridge cannot see or drive party companions.</summary>
    bool SupportsPartySync => false;

    /// <summary>
    /// Host side: reads the active party companions (keyed by NPC slot), whose movement
    /// the host's own engine AI drives. The client mirrors these.
    /// </summary>
    List<PartyMemberState> ReadPartyMembers() => [];

    /// <summary>
    /// The portrait resrefs of this instance's party, PC first — what a host advertises so a
    /// browsing player can see who they would be joining. Empty when unknown, which the
    /// browser draws as no faces rather than as an error.
    /// </summary>
    IReadOnlyList<string> ReadPartyPortraits() => [];

    /// <summary>
    /// Client side: freezes the local party AI (solo mode) so companions do not follow or
    /// act on their own, then mirrors the host's companions onto the matching local
    /// creatures by slot. Returns how many were placed. The local PC — even if it is a
    /// companion the player took control of after a death — is never mirrored.
    /// </summary>
    int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members) => 0;

    /// <summary>Client side: releases the solo-mode freeze this bridge asserted, so a
    /// single-player continuation after the session is normal. Called on shutdown.</summary>
    void ReleasePartyFreeze() { }

    /// <summary>False when this bridge cannot see or drive party gold / PC experience.</summary>
    bool SupportsProgressionSync => false;

    /// <summary>Host side: reads the party's gold and the PC's experience.</summary>
    bool TryReadProgression(out uint gold, out uint experience)
    {
        gold = 0;
        experience = 0;
        return false;
    }

    /// <summary>Client side: overwrites local gold and PC experience with the host's absolute
    /// values, so both parties progress together.</summary>
    bool TryWriteProgression(uint gold, uint experience) => false;
}
