// Encounter and generic-trigger sync: disarming local encounters, watching entry,
// and relaying fire requests.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IEncounterBridge
{
    /// <summary>
    /// Turns off the local module's encounters. Called on every peer that does NOT own
    /// enemies: encounter spawns are the one engine-side source of object-id drift, and
    /// identical ids across instances are the key combat replication rests on. Returns
    /// how many encounters were disabled (0 when already done for this module).
    /// </summary>
    int DisableLocalEncounters() => 0;

    /// <summary>
    /// Turns the plugin's encounter watch on or off. On only for a peer that does NOT own
    /// enemies: the owner's engine fires its own encounters natively and has nothing to ask
    /// anyone for, so the watch would be pure cost there.
    /// </summary>
    void SetEncounterWatch(bool enabled) { }

    /// <summary>
    /// Non-owner side: the object ids of the encounters the local player is standing inside
    /// right now, as the plugin's watch reports them (the engine's own polygon test). Empty
    /// when the plugin is absent or the player is in open ground.
    /// </summary>
    IReadOnlyList<uint> ReadEnteredEncounters() => [];

    /// <summary>
    /// Owner side: asks the plugin to run the engine's own "should this encounter fire"
    /// test for each (encounter, peer) pair, standing the peer's phantom in for the player
    /// who actually walked in. Repeats are harmless — the engine refuses an encounter that
    /// has already started. Returns false when there is no plugin.
    /// </summary>
    bool TryRequestEncounterFire(IReadOnlyList<(uint EncounterId, uint PeerId)> requests) => false;

    /// <summary>
    /// Non-owner side of TriggerRelay, mirroring <see cref="SetEncounterWatch"/>: run the
    /// generic-trigger watch only on a peer that is not enemy-authority. The owner's own
    /// engine already fires generic triggers for its own real character correctly and has
    /// nothing to ask anyone for.
    /// </summary>
    void SetTriggerWatch(bool enabled) { }

    /// <summary>
    /// Non-owner side: the object ids of the GENERIC triggers the local player is standing
    /// inside right now AND whose OnEnter script matched the coop.ini allowlist pattern, as
    /// the plugin's watch reports them. Empty when the plugin is absent, the player is
    /// outside every relay-eligible polygon, or the channel is disabled.
    /// </summary>
    IReadOnlyList<uint> ReadEnteredTriggers() => [];

    /// <summary>
    /// Every peer, protocol 36: the object ids of GENERIC triggers this instance's OWN engine
    /// has natively fired for its own real character since last read — the plugin's
    /// <c>TriggerFireHook</c> observer, already allowlist- and eligibility-filtered the same
    /// way <see cref="ReadEnteredTriggers"/>'s watch is. Unlike that watch (a currently-standing
    /// SET, level-published every frame), the plugin controller only adds an id here once it
    /// has actually been observed firing — see docs/research/trigger-relay.md's protocol-36
    /// section. Empty when the plugin is absent, nothing has fired natively yet, or the
    /// channel is disabled.
    /// </summary>
    IReadOnlyList<uint> ReadNativeFiredTriggers() => [];

    /// <summary>
    /// Owner side: asks the plugin to run the SAME OnEnter script the requesting peer's own
    /// engine already ran, against that peer's phantom standing in as the entering object.
    /// Unlike <see cref="TryRequestEncounterFire"/>, a repeat is NOT free here — a generic
    /// trigger carries no engine one-shot flag — so callers are expected to only ever pass
    /// requests <c>KotorCoop.Session.TriggerRelayStore.Collect</c> has not already marked
    /// fired. Returns false when there is no plugin.
    /// </summary>
    bool TryRequestTriggerFire(IReadOnlyList<(uint TriggerId, uint PeerId)> requests) => false;
}
