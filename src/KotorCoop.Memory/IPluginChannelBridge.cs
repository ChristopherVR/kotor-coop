// Direct in-process-plugin IPC channels that are not tied to one specific game
// concern: phantom-hit/incoming-damage, peer effects, creature liveness, and barks.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IPluginChannelBridge
{
    /// <summary>Damage a peer's phantom has taken in this game, as a running total. Default
    /// "nothing recorded" for bridges without an in-process plugin (the mock harness).</summary>
    bool TryReadPhantomHit(int slot, out uint phantomId, out uint cumulative, out uint attacker)
    {
        phantomId = 0;
        cumulative = 0;
        attacker = 0;
        return false;
    }

    /// <summary>Hands the plugin the running total this player owes for hits taken on their
    /// phantom elsewhere. No-op without a plugin.</summary>
    bool TryWriteIncomingDamage(uint cumulative, uint attacker) => false;

    /// <summary>Whether this bridge can also read the per-attacker breakdown alongside
    /// <see cref="TryReadPhantomHit"/> (SharedBlock version 62 and later). False lets a test
    /// fake or an older-shaped bridge implementation compile with no changes — the caller
    /// falls back to <see cref="TryReadPhantomHit"/>'s single cumulative total. See
    /// docs/research/peer-damage-ledger.md §7.</summary>
    bool SupportsPerAttackerPhantomHit => false;

    /// <summary>
    /// Up to 4 (attackerId, cumulativeDamage) pairs <c>PhantomHitHook</c> captured for one
    /// watched phantom slot, in the OWNER's id space — the same numbering
    /// <see cref="TryReadPhantomHit"/>'s attacker id already uses. Writes into
    /// <paramref name="attackerIds"/>/<paramref name="cumulativeDamage"/> (each expected at
    /// least <c>SharedBlock.PhantomAttackerCapacity</c> long) and returns how many entries
    /// were valid; 0 without a plugin or when <see cref="SupportsPerAttackerPhantomHit"/> is
    /// false.
    /// </summary>
    int ReadPhantomHitAttackers(int slot, Span<uint> attackerIds, Span<uint> cumulativeDamage) => 0;

    /// <summary>Hands the plugin the merged owed-per-attacker breakdown for the local player —
    /// summed across every peer that has reported hits on their phantom elsewhere (see
    /// <see cref="PhantomAttackerMerge"/>). No-op without a plugin.</summary>
    bool TryWriteIncomingDamageAttackers(IReadOnlyList<PhantomAttackerEntry> attackers) => false;

    /// <summary>Non-damage effects (stun, paralyze, knockdown, slow, frightened, sleep, choke,
    /// Force push/whirlwind) captured landing on this instance's phantoms since the plugin
    /// last published them. Empty for bridges without an in-process plugin. See
    /// docs/research/effect-sync.md.</summary>
    List<PeerEffectRecord> ReadPeerEffectsOut() => [];

    /// <summary>Hands the plugin one effect a peer's hook captured landing on this player's
    /// phantom over there, for it to replay on the real character here. No-op without a
    /// plugin.</summary>
    bool TryWritePeerEffectIn(uint originPeerId, uint kind, uint durationMs, uint magnitude, uint serial) => false;

    /// <summary>
    /// Every creature this engine currently knows about in the loaded area — not just the
    /// 32-slot combat census <see cref="ReadEnemies"/> caps to. Reuses the SAME confirmed
    /// enumeration <see cref="ReadEnemies"/> builds from (the ghost gate documented in
    /// docs/research/enemy-presence-sync.md), so an id published here is exactly as
    /// trustworthy as one published in the ordinary census. Excludes the player, party
    /// members (owned by other channels already) and any co-op phantom/materialised-stand-in
    /// body — never anyone's NPC to report on. Used by whichever peer owns enemies, for the
    /// liveness-reconciliation channel — see docs/research/creature-liveness.md.
    /// </summary>
    List<CreatureLivenessEntry> ReadCreatureLiveness() => [];

    /// <summary>Barks the plugin captured since the last look. See <see cref="BarkRecord"/>.</summary>
    List<BarkRecord> ReadBarks() => [];

    /// <summary>Hands a peer's bark to the plugin to replay on its copy of the speaker.</summary>
    bool TryWriteBark(
        uint speaker, ReadOnlySpan<byte> text, ReadOnlySpan<byte> soundResRef, uint flags, uint serial) => false;

    /// <summary>
    /// Non-owner side: hands the host's latest creature-liveness view to the plugin's
    /// reconciliation controller, so a body left alive here that the host says is dead or
    /// destroyed can be corrected through the ordinary kill/destroy paths. Capacity-limited
    /// (<c>SharedBlock.LivenessCapacity</c>); the caller should order the entries that need
    /// ACTING ON (dead/gone) first, since those are what a truncation must never drop. See
    /// docs/research/creature-liveness.md.
    /// </summary>
    bool TryWriteCreatureLiveness(IReadOnlyList<CreatureLivenessEntry> entries) => false;

    /// <summary>
    /// Non-owner side: how many "host says alive, but this instance's own engine already had
    /// it dead or gone" anomalies the plugin's reconciliation controller has noticed since it
    /// started — a cumulative counter, never reset by a module change. Never acted on beyond
    /// logging/counting (the right fix for a genuine anomaly is upstream, not a forced
    /// resurrection) — see docs/research/creature-liveness.md.
    /// </summary>
    uint ReadLivenessAnomalyCount() => 0;
}
