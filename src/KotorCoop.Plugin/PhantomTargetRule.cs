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

    /// <summary>The one reason worth naming in the log when the body goes Neutral: the peer
    /// is hiding, as opposed to dead / the authority split / the key being off.</summary>
    public static bool IsStealthHold(bool targetable, bool alive, bool holdNeutral, bool stealthSync, bool stealthed) =>
        targetable && alive && !holdNeutral && stealthSync && stealthed;
}
