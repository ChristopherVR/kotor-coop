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

    /// <summary>
    /// Recovery primitive: drops every agent-side CACHE (creature/scan addresses, item tag/
    /// address, module-name, portrait, world-state shadow dictionaries, ...) without touching
    /// the plugin block or any session state — unlike <see cref="ClearCoopState"/>, this is
    /// safe to call mid-session, with a session still connected, while the player is still in
    /// the world. Built for <c>CoopAgent</c>'s pump-failure loop: a corrupted Dictionary/List
    /// (see <see cref="IGameBridge.Sync"/>'s remarks) throws the SAME exception forever once
    /// corrupted, so retrying the read alone can never recover — replacing the cache with a
    /// fresh empty one can. Default is a no-op on bridges with nothing cached.
    /// </summary>
    void ResetCaches() { }

    /// <summary>Agent -> plugin. Publishes a valid local session role atomically: known host,
    /// known client, or unknown when no live session role can be proven. Default is a no-op on
    /// bridges without an in-process plugin block.</summary>
    void SetLocalSessionRole(bool sessionActive, bool isHost) { }

    /// <summary>Agent -> plugin. Publishes the duel the local player is in (docs/research/pvp-plan.md):
    /// <paramref name="phase"/> 0 none, 1 countdown (pin and snapshot), 2 live (the opponent's
    /// stand-in is hostile); <paramref name="opponentPeer"/> is the other duellist's peer id. A
    /// level, rewritten every pump; zeroed on disconnect. Default is a no-op.</summary>
    void SetPvpState(uint phase, uint opponentPeer, uint round) { }

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

    // ---- first-lightsaber colour (docs/research/class-and-power-grants.md §10) ----------

    /// <summary>
    /// This instance's own player's class ids, in slot order (0-2 entries, <c>classes.2da</c>
    /// rows) — <c>CSWSCreatureStats::classes</c>, read directly with <c>ReadProcessMemory</c>,
    /// no engine call. Used only by <c>FirstLightsaberColorRule</c>, on the receiving side of a
    /// Zhar saber-construction grant, to give each player their OWN class's saber colour rather
    /// than mirroring whichever colour the builder's own class happened to be. Empty when no
    /// world is loaded, the local player has no stats block, or this bridge has no such reader.
    /// </summary>
    IReadOnlyList<int> ReadLocalClassIds() => [];

    /// <summary>
    /// Pushes this session's RESOLVED player-count scaling into the plugin's shared block
    /// (docs/research/player-count-scaling.md): <paramref name="combatWord"/> carries the enemy
    /// health percent in its low 16 bits and the enemy damage percent in its high 16 — the two
    /// the plugin applies (hit points on first sighting, the damage rewrite at the
    /// OnApplyDamage detour); <paramref name="economyWord"/> carries xp and store stock the
    /// same way, for the plugin's status line only. <paramref name="pricesWord"/> carries the
    /// resolved retail buy/sell percent in its low half; 0 means unresolved and the plugin
    /// uses neutral 100%. 0 in a half means "no session has resolved
    /// this" and the plugin applies nothing, which is also the post-teardown state
    /// (<c>KotorGameBridge.SessionScopedBlockLevels</c>). Best-effort, like
    /// <see cref="ClearCoopState"/>; a no-op on a bridge with no plugin block.
    /// </summary>
    void SetPlayerCountScaling(uint combatWord, uint economyWord, uint pricesWord) { }
}
