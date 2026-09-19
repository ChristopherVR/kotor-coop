// Enemy census and combat sync: reading/publishing enemy state and the damage ledger.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IEnemyBridge
{
    /// <summary>False when this bridge cannot see or drive enemy health.</summary>
    bool SupportsEnemySync => false;

    /// <summary>
    /// Reads the module's creatures for the enemy channel. Used by whichever peer owns them.
    ///
    /// <paramref name="otherPlayers"/> is where the OTHER players are standing. The census is
    /// capped, and what the cap sheds is decided by distance — so without this the publisher
    /// only ever measures from itself, and the creatures around a partner across the module
    /// are the first thing dropped. See <see cref="KotorGameBridge.ReadEnemies"/>.
    /// </summary>
    List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers) => [];

    /// <summary>
    /// Applies the owning peer's enemy state to this instance: mirror their health, and
    /// stop local fighting from killing anything the owner still considers alive.
    /// </summary>
    bool TryWriteEnemies(IReadOnlyList<EnemyState> enemies) => false;

    /// <summary>Version 61: the owner ids of every mirrored enemy this instance is currently
    /// letting fight the local player for real under `local_combat` (up to
    /// SharedBlock.LocalCombatSlotCapacity/3 slots). The caller must keep every one of these
    /// ids out of what it writes back via <see cref="TryWriteEnemies"/> — see
    /// <c>KotorGameBridge.ReadLocalCombatExcludedIds</c> for why.</summary>
    IReadOnlyList<uint> ReadLocalCombatExcludedIds() => [];

    /// <summary>
    /// Version 65: the owner ids the plugin's own <c>MaterialisedEnemies</c> table currently
    /// maps to a LOCAL stand-in body — read once per pump so a materialised-loot give can be
    /// filtered before it costs a round trip on the single-slot giveloot cheat channel instead
    /// of only discovering the refusal after spending it (see
    /// <c>Session.MaterialisedLootFilterRule</c>). Null means UNKNOWN (no plugin attached — a
    /// version-mismatched agent/plugin pair simply never finds the block at all, so this is the
    /// only case that means "don't know") and callers MUST treat that as "send it anyway"; a
    /// real, if possibly empty, set licenses filtering by membership. See
    /// <c>KotorGameBridge.ReadMaterialisedOwnerIds</c> and
    /// docs/research/enemy-materialisation.md's 2026-09-02 section.
    /// </summary>
    IReadOnlySet<uint>? ReadMaterialisedOwnerIds() => null;

    /// <summary>
    /// Mirroring side: how much damage this instance has DEALT to each enemy since its
    /// object ids were last assigned, plus the ledger generation those totals belong to.
    /// Cumulative and monotonic within a generation; empty when no plugin is attached.
    /// </summary>
    (uint Generation, List<EnemyDamageEntry> Damage) ReadDamageDealt() => (0, []);

    /// <summary>
    /// Enemy-authority side only: hands the plugin the total every peer has dealt to each
    /// of our enemies, keyed to <paramref name="epoch"/> (the module the ids belong to).
    /// The plugin applies the increase as real engine damage on the game thread — which is
    /// also how a peer's kill happens, because an enemy whose accumulated damage exceeds
    /// its health simply dies, with the corpse, loot and OnDeath the engine runs for any
    /// other death.
    /// </summary>
    bool TryPublishDamageOwed(uint epoch, IReadOnlyList<EnemyDamageEntry> owed) => false;

    // ---- client-cast enemy effects (docs/research/enemy-effects.md, shared block 0x4C60) ----

    /// <summary>
    /// Mirroring side: the most recent non-damage effect the local player cast on a mirrored
    /// enemy, captured by the plugin and offered one at a time (the plugin holds the next until
    /// <see cref="TryAckEnemyEffectOut"/> echoes this one's serial). False when nothing is
    /// offered or there is no plugin. De-duplicate on <see cref="EnemyEffectSample.Serial"/>.
    /// </summary>
    bool TryReadEnemyEffectOut(out EnemyEffectSample sample)
    {
        sample = default;
        return false;
    }

    /// <summary>Tells the plugin the offered effect with this serial has been taken, so it can
    /// offer the next. False when there is no plugin.</summary>
    bool TryAckEnemyEffectOut(uint serial) => false;

    /// <summary>
    /// Enemy-authority side: hands the plugin one effect a client reported, to apply on the real
    /// enemy body. Only valid while <see cref="TryReadEnemyEffectApplied"/> has caught up to the
    /// previous write. False when there is no plugin.
    /// </summary>
    bool TryWriteEnemyEffectIn(in EnemyEffectSample sample) => false;

    /// <summary>The serial of the last inbound effect the plugin has handled (applied or
    /// refused). False when there is no plugin.</summary>
    bool TryReadEnemyEffectApplied(out uint serial)
    {
        serial = 0;
        return false;
    }
}
