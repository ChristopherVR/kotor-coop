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

    /// <summary>ROUND 39: the owner id of the mirrored enemy this instance is currently
    /// letting fight the local player for real under `local_combat`, or 0 for none. The
    /// caller must keep this id out of what it writes back via <see cref="TryWriteEnemies"/> —
    /// see <c>KotorGameBridge.ReadLocalCombatExcludedId</c> for why.</summary>
    uint ReadLocalCombatExcludedId() => 0;

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
}
