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

    /// <summary>Agent -> plugin. Publishes a valid local session role atomically: known host,
    /// known client, or unknown when no live session role can be proven. Default is a no-op on
    /// bridges without an in-process plugin block.</summary>
    void SetLocalSessionRole(bool sessionActive, bool isHost) { }

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
    /// and shows "<paramref name="ownerName"/> is in a solo section - waiting". A LEVEL, republished
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

    // ---- class-grant relay (docs/research/class-and-power-grants.md) ---------------

    /// <summary>
    /// The class a SCRIPT most recently added to THIS instance's own player through
    /// <c>AddMultiClass</c> (Dorak's Jedi path choice on Dantooine): <paramref name="serial"/>
    /// bumps once per grant (0 = never), and <paramref name="classId"/> is the
    /// <c>classes.2da</c> row. Published by the plugin from its capture of the VM handler; a
    /// replay of a peer's grant never bumps it. De-duplicate on the serial. Default false when
    /// this bridge has no plugin.
    /// </summary>
    bool TryReadClassGrant(out uint serial, out int classId)
    {
        serial = 0;
        classId = -1;
        return false;
    }

    /// <summary>
    /// Asks the plugin to add <paramref name="classId"/> to this instance's own player through
    /// the engine's own <c>AddMultiClass</c> handler — the same call the peer's script made.
    /// The plugin acks through <see cref="TryReadClassGrantStatus"/>. Default false when there
    /// is no plugin.
    /// </summary>
    bool TryRequestClassGrant(int classId) => false;

    /// <summary>
    /// What the plugin did with the last <see cref="TryRequestClassGrant"/> request:
    /// <paramref name="applied"/> echoes the request's own serial once handled, and
    /// <paramref name="status"/> is the plugin's <c>ClassGrantStatusCode</c> (1 applied,
    /// 2 engine refused, 3 bad class, 4 already had it, 5 both class slots taken).
    /// False when there is no plugin.
    /// </summary>
    bool TryReadClassGrantStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;
        return false;
    }

    /// <summary>
    /// Pushes this session's RESOLVED player-count scaling into the plugin's shared block
    /// (docs/research/player-count-scaling.md): <paramref name="combatWord"/> carries the enemy
    /// health percent in its low 16 bits and the enemy damage percent in its high 16 — the two
    /// the plugin applies (hit points on first sighting, the damage rewrite at the
    /// OnApplyDamage detour); <paramref name="economyWord"/> carries xp and store stock the
    /// same way, for the plugin's status line only. 0 in a half means "no session has resolved
    /// this" and the plugin applies nothing, which is also the post-teardown state
    /// (<c>KotorGameBridge.SessionScopedBlockLevels</c>). Best-effort, like
    /// <see cref="ClearCoopState"/>; a no-op on a bridge with no plugin block.
    /// </summary>
    void SetPlayerCountScaling(uint combatWord, uint economyWord) { }
}
