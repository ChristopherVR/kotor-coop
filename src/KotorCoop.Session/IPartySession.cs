// Companion party positions, the roster of who is recruited/active, and shared gold/XP.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IPartySession
{
    /// <summary>Host only: publish the authoritative party companions. Ignored on a client.</summary>
    void PublishParty(IReadOnlyList<PartyMemberState> members);
    void PublishParty(IReadOnlyList<PartyMemberState> members, uint capturedLoadSerial, uint capturedNativeEpoch)
        => PublishParty(members);

    /// <summary>Client only: the host's latest party companions. Empty on the host.</summary>
    IReadOnlyList<PartyMemberState> GetPartyMembers();

    /// <summary>
    /// Host only: publish the authoritative companion ROSTER — which of the nine NPC slots are
    /// recruited and which are in the active party. Ignored on a client.
    ///
    /// Separate from <see cref="PublishParty"/>, which carries where the companions ARE. This
    /// carries who they are, which is the thing that channel keys on: a companion recruited or
    /// dismissed inside a conversation changes only this, and until it crossed the wire the
    /// other instance kept resolving slot N to whatever it happened to have there.
    /// </summary>
    void PublishPartyRoster(PartyRoster roster) { }

    /// <summary>
    /// Client only: the host's latest roster. False until the first level has arrived — a
    /// client must never apply an all-zero roster it has merely not been told yet, which would
    /// dismiss its whole party.
    /// </summary>
    bool TryGetPartyRoster(out PartyRoster roster)
    {
        roster = PartyRoster.Empty;
        return false;
    }

    /// <summary>Host only: publish the party's gold and the PC's experience. Ignored on a client.</summary>
    void PublishProgression(uint gold, uint experience);

    /// <summary>
    /// Client only: this peer's contribution to the shared purse and its own experience total.
    ///
    /// Gold is a cumulative earned/spent LEDGER, never a balance — a balance cannot be merged
    /// (two instances holding 400 and 1200 have no arithmetic that says which is right) while
    /// "this peer has taken 250 out of the purse since it joined" can be applied exactly once
    /// by the host, in any order, however many times it arrives.
    ///
    /// Experience is ABSOLUTE and merges upward by max. It cannot be a delta: both engines
    /// already grant kill XP independently (a mirrored enemy dies through the real damage
    /// pipeline, so <c>ApplyDeathExperience</c> runs on the mirror too), so forwarding a delta
    /// would count every kill twice. What diverges is script-granted XP, and a max carries
    /// that without disturbing the rest.
    /// </summary>
    void ReportProgression(uint generation, uint goldEarned, uint goldSpent, uint experience) { }

    /// <summary>Host only: every client's latest progression contribution, fresh ones only.
    /// Empty on a client.</summary>
    IReadOnlyList<PeerProgression> GetProgressionReports() => [];

    /// <summary>
    /// Client only: the host's latest gold and experience. Returns false until a snapshot has
    /// arrived, so a client never mirrors zero over its own values before the first one.
    /// </summary>
    bool TryGetProgression(out uint gold, out uint experience);

    // ---- companion experience (2026-08-27, docs/research/companion-state-sync.md) ---------
    //
    // Same shape as the PC's own progression above, keyed by NPC roster slot. coop.ini
    // companion_xp_sync, ON by default (live-verified 2026-08-27).

    /// <summary>Host only: publish each active companion's absolute experience, keyed by NPC
    /// roster slot (a set bit in <paramref name="presentMask"/> means that slot's entry in
    /// <paramref name="xpBySlot"/> is meaningful). Ignored on a client.</summary>
    void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot) { }
    void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot, PartyRoster captured)
        => PublishCompanionExperience(presentMask, xpBySlot);

    /// <summary>Client only: this peer's own reading of each active companion's experience,
    /// for the host to merge upward by max against every other peer's.</summary>
    void ReportCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot) { }
    void ReportCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot, PartyRoster captured, uint localEpoch)
        => ReportCompanionExperience(presentMask, xpBySlot);

    /// <summary>Host only: every client's latest companion-experience report, fresh ones only.
    /// Empty on a client.</summary>
    IReadOnlyList<PeerCompanionXp> GetCompanionExperienceReports() => [];

    /// <summary>
    /// Client only: the host's latest per-companion experience. False until a snapshot has
    /// arrived, so a client never mirrors an all-absent mask over its own values before the
    /// first one.
    /// </summary>
    bool TryGetCompanionExperience(out ushort presentMask, out uint[] xpBySlot)
    {
        presentMask = 0;
        xpBySlot = new uint[ProtocolConstants.PartyRosterSlots];
        return false;
    }
}
