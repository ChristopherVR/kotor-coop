using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// Companion damage TAKEN by a companion companion_local_combat has released to fight through
// its own engine AI on a mirroring instance, forwarded to the host so its authoritative body
// actually takes it. The companion sibling of CombatPump.Enemies.cs's damage-DEALT half — same
// cadence (CoopAgent.PumpOnce calls this from the identical ~150ms combat-pump block
// PumpEnemies runs in), same "cumulative total, never health" shape, for the identical reason:
// docs/research/peer-damage-ledger.md and docs/research/companion-state-sync.md §13.
internal sealed partial class CombatPump
{
    /// <summary>Host side: every peer's cumulative damage against its own companions, folded
    /// into one monotonic total per NPC roster slot for our own engine to spend.</summary>
    private readonly CompanionDamageLedger _companionDamage = new();

    /// <summary>
    /// Moves companion-damage state between the game and the session in whichever direction
    /// this peer is responsible for — DOWN from the host is nothing (a companion's health is
    /// already carried whole by the ordinary party snapshot); UP from a mirroring peer is
    /// DAMAGE TAKEN, cumulatively, for the identical reason <see cref="PumpEnemies"/>'s own
    /// damage-dealt half reports a contribution rather than a sampled level.
    /// </summary>
    /// <param name="syncEnabled">coop.ini <c>companion_damage_sync</c> (default ON) — kept as a
    /// caller-supplied flag rather than a constructor dependency so this pump needs no change
    /// to <see cref="CombatPump"/>'s own construction; see <c>CoopAgent.PumpOnce</c>.</param>
    public void PumpCompanionDamage(bool syncEnabled)
    {
        if (!syncEnabled || !bridge.SupportsCompanionDamageSync)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // The host: turn every peer's reported rise against our own companions into real
            // damage on our own engine. Published every pass, even when empty — the total is a
            // LEVEL the plugin diffs against what it has already applied, so a pass the plugin
            // misses costs nothing and republishing the same table is free.
            bridge.TryPublishCompanionDamageOwed(_companionDamage.Merge(session.GetCompanionDamage()));
            return;
        }

        // A mirroring instance: report what our own engine's damage pipeline has actually
        // applied to whichever companion(s) companion_local_combat has released to us. Empty
        // is not published — nothing is released, or nothing has been hit yet — the same
        // "only report a genuine contribution" rule PumpEnemies' own dealt-report half follows.
        (uint generation, List<CompanionDamageEntry> taken) = bridge.ReadCompanionDamageTaken();
        if (taken.Count > 0)
        {
            session.ReportCompanionDamage(generation, taken);
        }
    }
}
