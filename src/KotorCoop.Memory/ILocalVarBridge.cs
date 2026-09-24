// Per-object local-variable replication (coop.ini local_var_sync, docs/research/quest-sync.md
// §8.3): draining the plugin's capture ring and handing the session's values back.
namespace KotorCoop.Memory;

public interface ILocalVarBridge
{
    /// <summary>False when this bridge has no plugin block, or the plugin reports its own
    /// <c>local_var_sync</c> off (the capture hook is then not installed).</summary>
    bool SupportsLocalVarSync => false;

    /// <summary>
    /// The allowlisted local-variable writes this engine made since the last call, as
    /// <see cref="KotorCoop.Protocol.LocalVarSyncRule"/> flag keys and normalised values, in
    /// capture order. Only captures stamped with the plugin's CURRENT world generation and the
    /// module this bridge currently reads are returned; the first call after attach is a silent
    /// baseline (nothing already in the ring is republished).
    /// </summary>
    List<KeyValuePair<ulong, sbyte>> CollectLocalVarCaptures() => [];

    /// <summary>
    /// Hands the plugin the session's values for the CURRENT module's local-variable keys,
    /// stamped with the plugin generation and the capture cursor drained so far. Other
    /// modules' keys are held back until this instance is in that module. Returns false when
    /// the block could not be written.
    /// </summary>
    bool TryWriteLocalVarStates(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags) => false;

    /// <summary>Capture-ring rows overwritten before a drain reached them, since attach.</summary>
    uint LocalVarCapturesLost => 0;

    /// <summary>How many entries the last successful <see cref="TryWriteLocalVarStates"/> handed
    /// the plugin (diagnostic: the agent logs when it changes).</summary>
    int LocalVarApplyCount => 0;
}
