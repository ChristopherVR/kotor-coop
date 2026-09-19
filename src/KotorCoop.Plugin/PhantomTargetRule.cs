namespace KotorCoop.Plugin;

/// <summary>
/// Whether a peer's stand-in should sit in the PLAYER faction (attackable by this instance's
/// enemies) this pass — the decision <c>PhantomController.ApplyTargetable</c> acts on, kept
/// pure so it can be unit-tested. Every input is a level the caller already has:
///
/// <list type="bullet">
/// <item><c>targetable</c> — coop.ini <c>phantom_targetable</c>.</item>
/// <item><c>alive</c> — the peer's published health is above zero.</item>
/// <item><c>holdNeutral</c> — the local_combat/DamageDealtEnabled authority split (see the
/// long comment in <c>ApplyTargetable</c>): a MIRRORING instance keeps every peer's body
/// Neutral so its own copies of the enemies fight the local PC.</item>
/// <item><c>stealthSync</c> / <c>stealthed</c> — coop.ini <c>stealth_sync</c> and the peer's
/// <c>PlayerState.FlagStealth</c> (protocol 53, effect-sync.md §12.4). A player who is in
/// stealth on their own screen was, before this rule, a plainly visible player-faction body
/// on their partner's — every hostile there could pick them, and the hits forwarded for real.
/// Holding the stand-in Neutral while the bit is set reuses the one release lever
/// <c>phantom_targetable</c> already pulls (faction flip + PacifyCreature), so no new engine
/// write is involved. The engine's own stealth VISUAL on the stand-in is a separate,
/// unverified write behind <c>stealth_visual_sync</c>.</item>
/// </list>
/// </summary>
internal static class PhantomTargetRule
{
    public static bool WantsPlayerFaction(
        bool targetable, bool alive, bool holdNeutral, bool stealthSync, bool stealthed) =>
        targetable && alive && !holdNeutral && !(stealthSync && stealthed);

    /// <summary>
    /// Which faction family the stand-in belongs to this pass, given the answer from
    /// <see cref="WantsPlayerFaction"/> and coop.ini <c>partner_buffs</c>
    /// (docs/research/partner-buffs.md).
    ///
    /// The whole point of <see cref="Kind.Friendly"/>: the engine's <c>GetIsFriend</c> is
    /// <c>GetReputation(caster, target) &gt;= 90</c>, and a body in the PLAYER faction reads
    /// <b>50</b> to a party member (<c>CFactionManager::GetNPCFactionReputation 0x0052B420</c>
    /// returns its hard-coded 50 at <c>0x0052B481</c> when BOTH faction ids are below 1 — Player
    /// against Player is not a matrix cell), so Force Valor's 30 m friend sphere skips it.
    /// Friendly_1 (id 2) reads 100 from the Player row and 0 from Hostile_1/Hostile_2/Insane/
    /// ptat_Tuskan/Zone_Controller/Sacrifice, so mainstream enemies still attack it. It is not
    /// an exact stand-in for Player — Czerka, Trap and Endar_Spire creatures read 50 (they stop
    /// attacking it) and Rancor and Friendly_2 read 0 (they start) — which is why the key is a
    /// default-OFF developer switch. When the body is NOT wanted in the player faction (peer
    /// down, stealthed, the local_combat authority split, phantom_targetable off) it stays
    /// Neutral whatever this key says: a Friendly_1 body would be attacked by Hostile_1, which is
    /// exactly what those holds exist to prevent.
    /// </summary>
    public enum Kind
    {
        Neutral = 0,
        Player = 1,
        Friendly = 2,
    }

    public static Kind FactionFor(bool wantsPlayerFaction, bool partnerBuffs) =>
        !wantsPlayerFaction ? Kind.Neutral : partnerBuffs ? Kind.Friendly : Kind.Player;

    /// <summary>The one reason worth naming in the log when the body goes Neutral: the peer
    /// is hiding, as opposed to dead / the authority split / the key being off.</summary>
    public static bool IsStealthHold(bool targetable, bool alive, bool holdNeutral, bool stealthSync, bool stealthed) =>
        targetable && alive && !holdNeutral && stealthSync && stealthed;
}
