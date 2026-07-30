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
}
