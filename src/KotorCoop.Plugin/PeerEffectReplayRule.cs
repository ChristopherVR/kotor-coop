namespace KotorCoop.Plugin;

/// <summary>
/// The decision half of <see cref="PeerEffectController"/>'s IN-ring replay latch, separated
/// so it can be unit-tested — the same split <see cref="PartyTogetherClampRule"/> uses.
///
/// The ring the agent writes (<c>TryWritePeerEffectIn</c>) is round-robin and never cleared:
/// an entry stays readable until the slot is reused. So "have I applied THIS entry" has to be
/// answered from a per-slot key, and the key has two parts:
/// <list type="bullet">
/// <item>the SERIAL — the sender's own monotonic <c>++_peerEffectSequence</c>, so a re-read of
/// an unchanged slot is not a second application;</item>
/// <item>the ORIGIN peer — because serials are per-sender, and with three players slot
/// <c>i</c> can hold peer A's #5 and, a few captures later, peer B's #5.</item>
/// </list>
///
/// ⚠ A WORLD EDGE ADOPTS, IT DOES NOT FORGET. Live 2026-09-03 00:48 (effect-sync.md): clearing
/// the latch at a module change re-applied the entire ring — minutes-old heals, buffs and a
/// 6 s <em>confused</em> — onto the freshly loaded character, on every arrival. Everything the
/// ring holds at the edge was captured for the world being torn down; it is marked applied
/// without being run.
/// </summary>
internal static class PeerEffectReplayRule
{
    /// <summary>One slot's identity: origin in the high word, serial in the low.</summary>
    public static ulong Key(uint originPeerId, uint serial) => ((ulong)originPeerId << 32) | serial;

    /// <summary>What a slot's latch becomes at a world edge: whatever the slot holds, so that
    /// exact entry is never replayed. An empty slot (serial 0) adopts 0, which
    /// <see cref="IsNew"/> already treats as "nothing to apply".</summary>
    public static ulong Adopt(uint originPeerId, uint serial) => serial == 0 ? 0 : Key(originPeerId, serial);

    /// <summary>Whether the entry now in the slot is one this instance has not applied: a
    /// non-zero serial whose (origin, serial) differs from the latch.</summary>
    public static bool IsNew(ulong appliedKey, uint originPeerId, uint serial) =>
        serial != 0 && Key(originPeerId, serial) != appliedKey;
}
