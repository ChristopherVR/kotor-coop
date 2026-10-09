// Enemy authority, the combat-census enemy list, and the wider liveness view that catches
// a body left alive on one side and dead/destroyed on the other. See docs/research/creature-liveness.md.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IEnemySession
{
    /// <summary>
    /// True when this peer owns enemy state. Exactly one peer does — the host — and it is
    /// the only one whose fights actually kill anything; everyone else mirrors it.
    /// </summary>
    bool IsEnemyAuthority { get; }

    /// <summary>Host only: publish the authoritative enemy list, with the module its object
    /// ids were read in. Ignored on a client.</summary>
    void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies);

    /// <summary>
    /// Client only: the host's latest enemy list, but only if the host read it in
    /// <paramref name="module"/> — object ids are per module load, so a snapshot taken
    /// somewhere else names different creatures here. Empty on the host, and empty
    /// whenever the two are apart.
    /// </summary>
    IReadOnlyList<EnemyState> GetEnemies(string module);

    /// <summary>
    /// Host only: publish every creature this instance's engine currently knows about in
    /// <paramref name="module"/> — not just the 32-slot combat census <see cref="PublishEnemies"/>
    /// carries — so a mirroring peer can notice a body left alive on one side and dead or
    /// destroyed on the other and reconcile it. Ignored on a client. See
    /// docs/research/creature-liveness.md.
    /// </summary>
    void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries) { }
    void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries, uint worldLoadSerial) =>
        PublishCreatureLiveness(module, entries);
    void InvalidateCreatureLivenessForLocalLoad() { }
    void InvalidateCreatureLivenessForWorldTransition() { }
    void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries, uint worldLoadSerial, uint nativeEpoch) =>
        PublishCreatureLiveness(module, entries, worldLoadSerial);

    /// <summary>
    /// Client only: the host's latest liveness view for <paramref name="module"/>, accumulated
    /// across however many pages the current publish cycle needed. Empty on the host (never a
    /// receiver of its own channel) and empty whenever the two are apart — same module gate as
    /// <see cref="GetEnemies"/>.
    /// </summary>
    IReadOnlyList<CreatureLivenessEntry> GetCreatureLiveness(string module) => [];

    /// <summary>
    /// Client only: <see cref="GetCreatureLiveness"/> plus when its last page arrived, read
    /// together. The time is null while the current cycle is still missing pages (a partial view
    /// can lack ids the host has). Null on the host.
    /// </summary>
    (IReadOnlyList<CreatureLivenessEntry> Entries, long? CompleteAtMs) GetCreatureLivenessView(string module) =>
        (GetCreatureLiveness(module), null);

    /// <summary>Host only: the blueprints of the creatures this host's scripts created in its
    /// current world (host spawns only). A no-op on a client.</summary>
    void PublishSpawnTemplates(string module, uint nativeEpoch, IReadOnlyList<SpawnTemplateEntry> entries) { }

    /// <summary>Client only: the host's spawn blueprints for <paramref name="module"/>.</summary>
    IReadOnlyList<SpawnTemplateEntry> GetSpawnTemplates(string module) => [];

    /// <summary>
    /// Pushes the plugin's cumulative liveness-anomaly count (see
    /// <c>KotorGameBridge.ReadLivenessAnomalyCount</c>) into <see cref="GetStats"/>'s
    /// <c>LivenessAnomalies</c> field, so <c>status</c> can show it. The session never
    /// computes this itself — it is purely a local diagnostic the agent reads out of the
    /// shared block and reports here every pump.
    /// </summary>
    void ReportLivenessAnomalyCount(uint count) { }
}
