// Local-player identity and lifecycle: attach status, reading/publishing the local
// player's state, session teardown, remote-slot bookkeeping, and the client control lock.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IPlayerBridge
{
    string Description { get; }

    bool IsAttached { get; }

    /// <summary>
    /// False when the profile has no remote slot layout. The session still works: the
    /// local player is broadcast and remote players are tracked, there is simply nowhere
    /// in the game to draw them yet.
    /// </summary>
    bool CanWriteRemotes { get; }

    bool TryReadLocalState(out PlayerState state);

    bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers);

    /// <summary>
    /// Tears down the in-game co-op state on disconnect: no remote peers, no phantoms, the
    /// party freeze released. So a disconnected player is left in a clean single-player
    /// game rather than staring at a frozen phantom of someone who left.
    /// </summary>
    void ClearCoopState() { }

    /// <summary>How many remote-player slots this bridge publishes into.</summary>
    int RemoteSlotCapacity => 0;

    /// <summary>
    /// Which peer owns a published slot, 0 for empty.
    ///
    /// ⚠ Read this rather than assuming "slot i is the i'th peer in the list you handed
    /// over". It is not: the list is filtered by module and masked by capability before it
    /// is written, and slots are sticky per peer so a disconnect does not renumber the
    /// survivors. Attributing a phantom's damage by list position instead of by this map
    /// bills the wrong player, and with three of them that is the normal case, not the
    /// edge case.
    /// </summary>
    uint PeerInSlot(int slot) => 0;

    /// <summary>Client side: pins player input control to the local PC so a client cannot take
    /// over the host-owned companions. Object-id write, crash-proof. False if not resolvable.</summary>
    bool LockControlToPlayer() => false;

    /// <summary>
    /// Publishes this tick's minigame peer-lock decision (<c>KotorCoop.Protocol
    /// .MinigameLockRule</c>) into the plugin's shared block: while
    /// <paramref name="locked"/> is true the plugin holds this instance's own player still
    /// and shows "<paramref name="ownerName"/> is racing - waiting". A LEVEL, republished
    /// whenever it changes; best-effort, like <see cref="ClearCoopState"/>, and a no-op on a
    /// bridge with no plugin block. See docs/research/minigames.md §24.
    /// </summary>
    bool TryWriteMinigameLock(bool locked, string ownerName) => false;

    /// <summary>
    /// Pushes this session's effective behaviour-override bits into the plugin's shared block
    /// — docs/research/mod-compatibility.md's "Behaviour-flag negotiation" gap, closed
    /// 2026-08-27: <c>KotorCoop.Session.SessionOverrides</c> already computes which of the
    /// five PLUGIN-gated coop.ini keys (effect_sync, trigger_relay, liveness_sync,
    /// map_reveal_sync, dialogue_recruit_sync) a host-authoritative comparison overrode, but
    /// nothing carried that into the SEPARATE plugin process until this. Bit N of
    /// <paramref name="overrideMask"/> set means the plugin should use bit N of
    /// <paramref name="overrideValues"/> instead of its own coop.ini for that key — see
    /// <see cref="BehaviourOverrideBits"/> for the bit assignment. Best-effort, like
    /// <see cref="ClearCoopState"/>; a no-op on a bridge with no plugin block.
    /// </summary>
    void SetBehaviourOverrides(uint overrideMask, uint overrideValues) { }
}
