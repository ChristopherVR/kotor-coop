namespace KotorCoop.Protocol;

/// <summary>
/// Pure decision functions for the peer-effect channel — the allowlist mapping, the dedup
/// watermark, and the ring-index advance — kept separate from the unsafe engine-memory code
/// that calls them so they can be unit-tested directly. See docs/research/effect-sync.md for
/// the derivation of every numeric pair below.
///
/// ⚠ <c>PhantomEffectHook.cs</c> (NativeAOT, cannot reference this assembly) carries its OWN
/// copy of <see cref="TryMapAllowlist"/>'s table, for the same reason <c>KotorGameBridge.cs</c>
/// carries its own copy of <c>SharedBlock</c>'s offsets: one number in two files. Keep them in
/// sync by hand; a mismatch means the plugin captures an effect this table would refuse to
/// name, which the receiving side simply never applies (an unmapped kind is a no-op, not a
/// crash) — annoying, not dangerous.
///
/// The desync this comment used to warn about (this copy briefly ahead of
/// <c>PhantomEffectHook.cs</c>'s own copy on sub-codes 5/6) has since closed — see
/// docs/research/effect-sync.md. Both copies list every allowlist member today (twelve as of
/// §10, 2026-09-02), capture and apply alike.
/// </summary>
public static class PeerEffectRules
{
    /// <summary>Ring capacity, both directions. Matches <c>SharedBlock.PeerEffectCapacity</c>.</summary>
    public const int RingCapacity = 8;

    /// <summary>
    /// Maps a VERIFIED (engine effect type, sub-code) pair to the wire's small numeric
    /// identity, or refuses (returns false) for anything not on the allowlist — including the
    /// three <c>OnApplySetState</c> sub-codes this pass could not name (effect-sync.md §3.2).
    /// <paramref name="subCode"/> is only meaningful when <paramref name="engineType"/> is 8
    /// (the shared state-effect handler); pass 0 for direct-type effects like Knockdown.
    /// </summary>
    public static bool TryMapAllowlist(ushort engineType, int subCode, out PeerEffectKind kind)
    {
        switch (engineType)
        {
            case 8: // OnApplySetState — the shared state-effect family
                switch (subCode)
                {
                    case 1: kind = PeerEffectKind.Confused; return true;    // EffectConfused (effect-sync.md §10)
                    case 3: kind = PeerEffectKind.Stun; return true;        // EffectDroidStun
                    case 5: kind = PeerEffectKind.Paralyze; return true;    // EffectParalyze (effect-sync.md §7)
                    case 6: kind = PeerEffectKind.Sleep; return true;       // EffectSleep (effect-sync.md §7)
                    case 7: kind = PeerEffectKind.Choke; return true;       // EffectChoke
                    case 8: kind = PeerEffectKind.Frightened; return true;  // EffectHorrified
                    case 10: kind = PeerEffectKind.Whirlwind; return true;  // EffectWhirlWind
                    // 2 (EffectFrightened — a DISTINCT native from the EffectHorrified this
                    // table calls "Frightened" above) and 4 (EffectStunned, the
                    // general/non-droid stun) are NAMED (effect-sync.md §7) but not on the
                    // wire allowlist — no PeerEffectKind exists for them yet.
                    default: kind = PeerEffectKind.None; return false;
                }

            case 15: kind = PeerEffectKind.TemporaryHitpoints; return true; // OnApplyTemporaryHitpoints (effect-sync.md §10)
            case 20: kind = PeerEffectKind.Knockdown; return true;  // OnApplyKnockdown
            case 29: kind = PeerEffectKind.Slow; return true;       // OnApplyMovementSpeedDecrease
            case 39: kind = PeerEffectKind.Heal; return true;       // OnApplyHeal (effect-sync.md §10)
            case 60: kind = PeerEffectKind.ForcePush; return true;  // OnApplyForcePush

            default:
                kind = PeerEffectKind.None;
                return false;
        }
    }

    /// <summary>
    /// Which kinds have a real apply-side constructor today (<c>OdysseyEffects</c>). Per
    /// effect-sync.md §7 that was all nine original allowlist members — Knockdown, Paralyze
    /// and Sleep all route through the shared <c>ExecuteCommandEffectComplex</c> handler at
    /// their own VERIFIED routine numbers (134, 148, 154) — and effect-sync.md §10
    /// (2026-09-02) adds Heal, TemporaryHitpoints (one-int-parameter constructors, same shape
    /// as Slow) and Confused (a fourth member of the Knockdown/Paralyze/Sleep
    /// <c>ExecuteCommandEffectComplex</c> family, routine 157). Kept here, not just in
    /// <c>OdysseyEffects</c>, so a test can assert the two lists agree without touching
    /// unmanaged code.
    /// </summary>
    public static bool HasApplyConstructor(PeerEffectKind kind) => kind switch
    {
        PeerEffectKind.Stun => true,
        PeerEffectKind.Paralyze => true,
        PeerEffectKind.Knockdown => true,
        PeerEffectKind.Slow => true,
        PeerEffectKind.Frightened => true,
        PeerEffectKind.Sleep => true,
        PeerEffectKind.Choke => true,
        PeerEffectKind.ForcePush => true,
        PeerEffectKind.Whirlwind => true,
        PeerEffectKind.Heal => true,
        PeerEffectKind.TemporaryHitpoints => true,
        PeerEffectKind.Confused => true,
        _ => false,
    };

    /// <summary>
    /// Which of a ring's captured entries are NEW since <paramref name="watermark"/> — the
    /// dedup a global monotonic capture serial makes possible without per-slot bookkeeping.
    /// Entries at or below the watermark are dropped (already sent or stale); the returned
    /// watermark is the highest serial seen, whether or not everything above the old watermark
    /// fit in this one ring read (a burst larger than <see cref="RingCapacity"/> between two
    /// reads costs the entries in between — the accepted "unreliable, transient" cost of this
    /// channel, same as a dropped bark).
    /// </summary>
    public static (IReadOnlyList<uint> NewIndices, uint NewWatermark) SelectNew(
        IReadOnlyList<uint> serials, uint watermark)
    {
        List<uint> newIndices = [];
        uint highest = watermark;

        for (int i = 0; i < serials.Count; i++)
        {
            uint serial = serials[i];
            if (serial == 0 || serial <= watermark)
            {
                continue;
            }

            newIndices.Add((uint)i);
            if (serial > highest)
            {
                highest = serial;
            }
        }

        return (newIndices, highest);
    }

    /// <summary>The next slot a round-robin ring writes into. Trivial, but named and tested
    /// so the plugin's unsafe copy and the agent's have one agreed definition of "wraps".</summary>
    public static int NextRingIndex(int current) =>
        ((current % RingCapacity) + RingCapacity + 1) % RingCapacity;
}
