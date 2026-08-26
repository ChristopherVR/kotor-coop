// World state: current module, global-variable replication, save/module identity
// digests, and world-load/session-load requests.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IWorldStateBridge
{
    /// <summary>
    /// The module the world is in RIGHT NOW, read directly from the engine — not retained
    /// from an earlier successful read the way <see cref="TryReadLocalState"/>'s caller
    /// retains a whole state across a failed one. Callers that label world-scoped data
    /// (the enemy census) must label it with this, read in the same breath as the data:
    /// a retained label and a live payload can belong to different worlds around a module
    /// transition. False when the bridge has no module reading; callers then fall back to
    /// the retained state's module, which is the pre-existing behaviour.
    /// </summary>
    bool TryReadCurrentModule(out string module)
    {
        module = string.Empty;
        return false;
    }

    /// <summary>
    /// A digest of the global-variable catalogue (0 when unresolved or unsupported).
    /// Exchanged in-world so two saves whose catalogues differ — whose (type, index) keys
    /// would name different variables — do not silently corrupt each other's quest state.
    /// </summary>
    uint GlobalsCatalogueDigest => 0;

    /// <summary>
    /// A digest of the identity of the save this session is playing — its module, its
    /// Mod_NextObjId counters and its stored GIT object-id list (0 when unknown). Object
    /// ids are only identical across instances because both engines dealt them from the
    /// same save, so this is what the enemy/door/item channels' id assumption rests on.
    /// </summary>
    uint SaveIdentityDigest => 0;

    /// <summary>
    /// A digest of the object ids the named module's GIT hands out — from the save's
    /// stored module state when this save has visited it, else predicted from the pristine
    /// module file the engine would load (0 when unknown). Judged per module, because a
    /// mod that edits one module's placements shifts ids there and nowhere else.
    /// </summary>
    uint ModuleGitDigest(string module) => 0;

    /// <summary>
    /// Roadmap item 14 phase 2's load-detection edge: the plugin's own count of world
    /// teardowns it judged UNANNOUNCED — a Load Game action, not an ordinary module
    /// transition. See <c>KotorCoop.Plugin.WorldLoadRule</c> for the primitive and
    /// <c>SharedBlock.OffsetWorldLoadSerial</c> for the wire field (block version 49). 0 for
    /// bridges without an in-process plugin (the mock harness) — indistinguishable from "no
    /// loads observed yet", which is the correct default for either case.
    /// </summary>
    uint WorldLoadSerial => 0;

    /// <summary>
    /// Roadmap item 14 phase 2's rejoin action: requests the plugin replay the same adopted-
    /// world load <c>agent adopt</c> uses for the first join. False (with a reason) for
    /// bridges that cannot drive the plugin at all (the mock harness).
    /// </summary>
    bool TryRequestAdoptedWorldLoad(out string diagnostic)
    {
        diagnostic = "no in-process plugin to request a load from";
        return false;
    }

    /// <summary>Polls the outcome of the last <see cref="TryRequestAdoptedWorldLoad"/>.</summary>
    bool TryGetAdoptedWorldLoadStatus(out uint applied, out int status)
    {
        applied = 0;
        status = 0;
        return false;
    }

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
}
