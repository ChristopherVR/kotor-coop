// The party-select relay: a client's own suppressed CSWGuiPartySelection screen, asked of the
// host instead. See docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IPartySelectSession
{
    /// <summary>
    /// Sends this player's own captured party-select request to the host — one-shot, no
    /// wire-level retry (the caller's own pump timeout covers a lost datagram, the same
    /// convention <see cref="IInteractionLockSession.RequestContainerLock"/> uses). A HOST's own
    /// implementation still accepts this (symmetry, and testability), applying the identical
    /// <c>PartySelectHostRequestRule</c> a peer's packet would go through, entirely in-process.
    /// Default no-op for a session that predates this.
    /// </summary>
    void RequestPartySelect(
        string module, string exitScript, int forceNpc1, int forceNpc2, uint requestSerial)
    { }

    /// <summary>
    /// Responses to this instance's own <see cref="RequestPartySelect"/> calls since the last
    /// poll, oldest first. A caller only cares about NEW responses for the request it is
    /// currently tracking; anything for an older, already-abandoned <c>requestSerial</c> is the
    /// caller's own to discard. Default empty for a session that predates this.
    /// </summary>
    IReadOnlyList<PartySelectResponsePacket> DrainPartySelectResponses() => [];

    /// <summary>
    /// HOST only: party-select requests received from peers since the last poll, oldest first.
    /// A client's implementation always returns empty — nobody sends a client this packet.
    /// Default empty for a session that predates this.
    /// </summary>
    IReadOnlyList<PartySelectRequestPacket> DrainPartySelectRequests() => [];

    /// <summary>
    /// HOST only: answers a peer's <see cref="PartySelectRequestPacket"/> — unicast to the
    /// peer that asked. Default no-op for a session that predates this, or when called on a
    /// client (nobody to answer).
    /// </summary>
    void SendPartySelectResponse(uint peerId, PartySelectResponsePacket response) { }
}
