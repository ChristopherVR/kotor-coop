using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// The seam between the co-op stack and a specific game. Everything above this interface
/// is game-agnostic; everything game-specific is an offset profile behind it.
/// </summary>
public interface IGameBridge : IDisposable
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
    /// False when this bridge has no way to materialise ground items in the game. The
    /// defaults make item sync purely opt-in so profile bridges need no changes.
    /// </summary>
    bool SupportsGroundItems => false;

    /// <summary>
    /// Declares the desired set of items on the ground; the bridge reconciles the game
    /// toward it (spawn what is missing, despawn what is no longer wanted).
    /// </summary>
    bool TryWriteGroundItems(IReadOnlyList<GroundItem> items) => false;

    /// <summary>
    /// Item ids the game reports as locally vanished (picked up by this player) since the
    /// last call. The caller announces these to the session.
    /// </summary>
    List<uint> CollectVanishedItems() => [];

    /// <summary>
    /// A digest of the global-variable catalogue (0 when unresolved or unsupported).
    /// Exchanged in-world so two saves whose catalogues differ — whose (type, index) keys
    /// would name different variables — do not silently corrupt each other's quest state.
    /// </summary>
    uint GlobalsCatalogueDigest => 0;

    /// <summary>False when this bridge cannot see the game's world/quest state.</summary>
    bool SupportsWorldState => false;

    /// <summary>
    /// World-state variables that changed locally since the last call, as (flag id, value).
    /// These are the quest and story booleans and counters the whole game branches on.
    /// </summary>
    List<KeyValuePair<uint, sbyte>> CollectChangedWorldState() => [];

    /// <summary>
    /// Applies a peer's world-state value. Must also update the bridge's own shadow copy,
    /// or the next scan sees it as a local change and broadcasts it straight back.
    /// </summary>
    bool TryApplyWorldState(uint flagId, sbyte value) => false;

    /// <summary>False when this bridge cannot see or drive door state.</summary>
    bool SupportsDoorSync => false;

    /// <summary>
    /// Door states that advanced locally since the last call (opened, unlocked,
    /// destroyed), as door-typed world flags ready for the reliable flag channel.
    /// </summary>
    List<KeyValuePair<uint, sbyte>> CollectChangedDoorState() => [];

    /// <summary>
    /// Publishes the session's door truth for the local module into the game. The bridge
    /// filters: flags for doors in other modules are held back until that module loads.
    /// </summary>
    bool TryWriteDoorStates(IReadOnlyList<KeyValuePair<uint, sbyte>> flags) => false;

    /// <summary>False when this bridge cannot see or drive placeable (chest/corpse) state.</summary>
    bool SupportsPlaceableSync => false;

    /// <summary>
    /// Placeable states that advanced locally since the last call (opened, unlocked), as
    /// placeable-typed world flags ready for the reliable flag channel.
    /// </summary>
    List<KeyValuePair<uint, sbyte>> CollectChangedPlaceableState() => [];

    /// <summary>Publishes the session's placeable truth for the local module into the game.</summary>
    bool TryWritePlaceableStates(IReadOnlyList<KeyValuePair<uint, sbyte>> flags) => false;

    /// <summary>False when this bridge cannot see or drive container (chest/corpse) contents.</summary>
    bool SupportsContainerLoot => false;

    /// <summary>
    /// Host side: items that left a shared container locally since the last call, as
    /// (container, item) object-id pairs. The caller announces these so the same item is
    /// removed from every other instance's copy of the container and cannot be taken twice.
    /// </summary>
    List<ContainerLootRemoval> CollectContainerLootRemovals() => [];

    /// <summary>
    /// Client side: declares the container-loot removals the session knows about, which the
    /// bridge applies to the game's own containers (deleting each looted item). Idempotent.
    /// </summary>
    bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals) => false;

    /// <summary>False when this bridge cannot mirror a peer's pause into the game.</summary>
    bool SupportsPauseSync => false;

    /// <summary>
    /// Holds (or releases) a pause on the peers' behalf. Either player pausing pauses
    /// both worlds — anything else lets one side act in a world the other believes is
    /// frozen.
    /// </summary>
    bool TrySetRemotePause(bool desired) => false;

    /// <summary>False when this bridge cannot warp the local player between modules.</summary>
    bool SupportsTravel => false;

    /// <summary>
    /// Sends the local player to another module through the game's own transition path.
    /// This is what keeps a client in the host's module — travelling together is the
    /// difference between shared co-op and two parallel campaigns.
    /// </summary>
    bool TryRequestTravel(string module, string waypoint = "") => false;

    /// <summary>False when this bridge cannot see or drive enemy health.</summary>
    bool SupportsEnemySync => false;

    /// <summary>
    /// True while the local player is in a conversation. Enemy state must not be published
    /// during one: the dialogue system disarms every hostile in this instance, and a peer
    /// mirroring that would see its own fight go quiet.
    /// </summary>
    bool IsInDialogue => false;

    /// <summary>
    /// Reads what the local player is currently hearing in a conversation, for the
    /// dialogue-spectate payload. Empty when not in a conversation, or when the plugin could
    /// not resolve the live line. Default is "no dialogue read" for bridges without a plugin.
    /// </summary>
    bool TryReadLocalDialogue(out string speaker, out string line)
    {
        speaker = string.Empty;
        line = string.Empty;
        return false;
    }

    /// <summary>Posts one line to the always-on chat overlay (used to surface a peer's
    /// dialogue). Default false when this bridge has no overlay.</summary>
    bool PostChatLine(string line) => false;

    /// <summary>Reads every enemy in the module. Used by whichever peer owns them.</summary>
    List<EnemyState> ReadEnemies() => [];

    /// <summary>
    /// Turns off the local module's encounters. Called on every peer that does NOT own
    /// enemies: encounter spawns are the one engine-side source of object-id drift, and
    /// identical ids across instances are the key combat replication rests on. Returns
    /// how many encounters were disabled (0 when already done for this module).
    /// </summary>
    int DisableLocalEncounters() => 0;

    /// <summary>
    /// Applies the owning peer's enemy state to this instance: mirror their health, and
    /// stop local fighting from killing anything the owner still considers alive.
    /// </summary>
    bool TryWriteEnemies(IReadOnlyList<EnemyState> enemies) => false;

    /// <summary>False when this bridge cannot see or drive party companions.</summary>
    bool SupportsPartySync => false;

    /// <summary>
    /// Host side: reads the active party companions (keyed by NPC slot), whose movement
    /// the host's own engine AI drives. The client mirrors these.
    /// </summary>
    List<PartyMemberState> ReadPartyMembers() => [];

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

    /// <summary>
    /// Enemy-authority side only: lowers own enemies to a peer's reported health where
    /// the peer's is lower. This is what makes a client's damage real. Returns how many
    /// enemies were lowered.
    /// </summary>
    int TryApplyEnemyFloors(IReadOnlyList<EnemyState> reports) => 0;

    /// <summary>False when this bridge cannot see or drive the quest journal.</summary>
    bool SupportsJournalSync => false;

    /// <summary>Journal entries that advanced locally since the last call.</summary>
    List<KeyValuePair<string, int>> CollectChangedJournal() => [];

    /// <summary>
    /// Advances a journal entry to a peer's state through the game's own path. Must be a
    /// no-op (returning false) when the game is already at or past that state.
    /// </summary>
    bool TryApplyJournalEntry(string questTag, int state) => false;
}
